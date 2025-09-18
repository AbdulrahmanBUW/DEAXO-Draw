# -*- coding: utf-8 -*-
from Autodesk.Revit.DB import *
from Autodesk.Revit.UI.Selection import ObjectType, ISelectionFilter
from pyrevit import revit, script, forms, EXEC_PARAMS
import System

uidoc = __revit__.ActiveUIDocument
doc = uidoc.Document
output = script.get_output()

def face_corners(face):
    """Return corner XYZ of a face's bounding box (BoundingBoxUV -> Evaluate)."""
    try:
        bb = face.GetBoundingBox()
        uv_min = bb.Min
        uv_max = bb.Max
        uvs = [UV(uv_min.U, uv_min.V), UV(uv_max.U, uv_min.V),
               UV(uv_max.U, uv_max.V), UV(uv_min.U, uv_max.V)]
        return [face.Evaluate(uv) for uv in uvs]
    except:
        return []

def face_centroid(face):
    corners = face_corners(face)
    if not corners:
        return None
    x = sum(p.X for p in corners) / len(corners)
    y = sum(p.Y for p in corners) / len(corners)
    z = sum(p.Z for p in corners) / len(corners)
    return XYZ(x, y, z)

def face_normal_from_corners(face):
    c = face_corners(face)
    if len(c) < 3:
        return None
    v1 = c[1] - c[0]
    v2 = c[2] - c[0]
    try:
        n = v1.CrossProduct(v2)
        if n.IsZeroLength():
            return None
        return n.Normalize()
    except:
        return None

def largest_vertical_faces_from_element(elem):
    """
    Return list of (Reference, centroid) for vertical faces, sorted by descending area.
    More robust geometry options and instance handling to ensure face.Reference is present.
    """
    opts = Options()
    opts.ComputeReferences = True
    opts.IncludeNonVisibleObjects = True
    opts.DetailLevel = ViewDetailLevel.Fine
    opts.View = doc.ActiveView

    faces_info = []
    try:
        geom = elem.get_Geometry(opts)
    except:
        geom = None

    if geom is None:
        # Try fallback without view restriction
        try:
            opts2 = Options()
            opts2.ComputeReferences = True
            opts2.IncludeNonVisibleObjects = True
            opts2.DetailLevel = ViewDetailLevel.Fine
            geom = elem.get_Geometry(opts2)
        except:
            geom = None

    if geom is None:
        if EXEC_PARAMS.debug_mode:
            print("No geometry for element Id:", elem.Id)
        return []

    # Flatten solids (handle GeometryInstance too)
    solids = []
    for g in geom:
        if isinstance(g, Solid):
            if g.Faces and g.Faces.Size > 0:
                solids.append(g)
        elif isinstance(g, GeometryInstance):
            try:
                inst_geom = g.GetInstanceGeometry()
                for gg in inst_geom:
                    if isinstance(gg, Solid) and gg.Faces and gg.Faces.Size > 0:
                        solids.append(gg)
            except:
                continue

    for s in solids:
        for face in s.Faces:
            # ensure face has a Reference (required for dimensions)
            try:
                ref = face.Reference
            except:
                ref = None
            if not ref:
                continue

            centroid = face_centroid(face)
            if centroid is None:
                continue

            normal = face_normal_from_corners(face)
            if normal is None:
                continue

            # Check vertical by normal's Z component (near zero => vertical)
            # Use strict threshold to avoid slanted faces
            if abs(normal.Z) < 0.2:
                try:
                    area = float(face.Area)
                except:
                    area = 0.0
                faces_info.append((face, ref, centroid, area))

    # Sort by area desc and return (Reference, centroid)
    faces_info.sort(key=lambda x: x[3], reverse=True)
    return [(fi[1], fi[2]) for fi in faces_info]

def get_mullion_instances_for_wall(wall):
    """Return mullion FamilyInstances that are hosted by given wall (if any)."""
    try:
        coll = FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_CurtainWallMullions).OfClass(FamilyInstance)
    except:
        return []
    mullions = []
    for m in coll:
        try:
            host = m.Host
            if host and host.Id == wall.Id:
                mullions.append(m)
        except:
            continue
    return mullions

class WallSelectionFilter(ISelectionFilter):
    def AllowElement(self, element):
        return isinstance(element, Wall)
    def AllowReference(self, reference, point):
        return True

try:
    # Let user pick walls. If they cancel, fallback to all walls in active view.
    try:
        with forms.WarningBar(title='Select walls (Esc to select all walls in view)'):
            refs = uidoc.Selection.PickObjects(ObjectType.Element, WallSelectionFilter())
        # refs are Reference objects; convert safely to elements
        selected_walls = []
        for r in refs:
            try:
                # If r has ElementId property, use it
                eid = getattr(r, "ElementId", None)
                if eid:
                    selected_walls.append(doc.GetElement(eid))
                else:
                    # fallback: doc.GetElement can accept Reference in some contexts
                    selected_walls.append(doc.GetElement(r))
            except:
                continue
    except Exception:
        selected_walls = FilteredElementCollector(doc, doc.ActiveView.Id).OfClass(Wall).ToElements()

    if not selected_walls:
        forms.alert("No walls found or selected.", exitscript=True)

    # Build refs + centroids
    refs_and_pts = []  # list of tuples (Reference, XYZ)

    for w in selected_walls:
        # Check mullions first (curtain wall scenario)
        mullions = get_mullion_instances_for_wall(w)
        if mullions:
            for m in mullions:
                faces = largest_vertical_faces_from_element(m)
                if faces:
                    ref, centroid = faces[0]  # largest vertical face of mullion
                    refs_and_pts.append((ref, centroid))
            continue

        # Regular wall: get its largest vertical face
        faces = largest_vertical_faces_from_element(w)
        if faces:
            ref, centroid = faces[0]
            refs_and_pts.append((ref, centroid))
        else:
            if EXEC_PARAMS.debug_mode:
                print("No suitable vertical face for wall Id:", w.Id)

    if EXEC_PARAMS.debug_mode:
        print("Collected reference count:", len(refs_and_pts))

    if len(refs_and_pts) < 2:
        forms.alert("Need at least two wall faces/mullions to create an aligned dimension.", exitscript=True)

    # Order points by dominant axis (span X vs span Y)
    pts = [p for (_, p) in refs_and_pts]
    xs = [p.X for p in pts]
    ys = [p.Y for p in pts]
    span_x = max(xs) - min(xs)
    span_y = max(ys) - min(ys)
    if span_x >= span_y:
        refs_and_pts.sort(key=lambda rp: (rp[1].X, rp[1].Y))
    else:
        refs_and_pts.sort(key=lambda rp: (rp[1].Y, rp[1].X))

    # Build ReferenceArray in sorted order
    ref_array = ReferenceArray()
    for r, p in refs_and_pts:
        ref_array.Append(r)

    # Compute chain direction and perpendicular (XY)
    first_pt = refs_and_pts[0][1]
    last_pt  = refs_and_pts[-1][1]
    chain_dir = XYZ(last_pt.X - first_pt.X, last_pt.Y - first_pt.Y, 0)
    if chain_dir.IsZeroLength():
        chain_dir = XYZ(1, 0, 0)
    chain_dir_n = chain_dir.Normalize()
    perp = XYZ(-chain_dir_n.Y, chain_dir_n.X, 0)  # 90 deg rotated in XY

    # Decide which side is "outside": compute centroid of all pts
    centroid_all = XYZ(sum(p.X for p in pts)/len(pts),
                       sum(p.Y for p in pts)/len(pts),
                       sum(p.Z for p in pts)/len(pts))
    mid_chain = (first_pt + last_pt) / 2.0
    to_centroid = centroid_all - mid_chain

    # If perp points toward centroid, flip so it points away (placing dims outside)
    if perp.DotProduct(to_centroid) > 0:
        perp = perp.Negate()

    # Compute offset distance automatically:
    long_span = max(span_x, span_y)
    offset_dist = (long_span * 0.05) + 0.2  # 0.2 ft margin
    if offset_dist < 0.2:
        offset_dist = 0.2

    offset_vec = perp.Multiply(offset_dist)

    # dimension line endpoints slightly extended beyond first/last for neatness
    extend = max(long_span * 0.01, 0.1)
    dim_start = XYZ(first_pt.X, first_pt.Y, first_pt.Z) - chain_dir_n.Multiply(extend) + offset_vec
    dim_end   = XYZ(last_pt.X, last_pt.Y, last_pt.Z) + chain_dir_n.Multiply(extend) + offset_vec
    dim_line = Line.CreateBound(dim_start, dim_end)

    # Create the dimension in a transaction
    with revit.Transaction("Auto Aligned Dimensions - Walls & Mullions (Auto-placed)"):
        new_dim = doc.Create.NewDimension(doc.ActiveView, dim_line, ref_array)

    forms.alert("Aligned dimension created for {} references.".format(ref_array.Size))

except Exception as e:
    if EXEC_PARAMS.debug_mode:
        import traceback
        print(traceback.format_exc())
    System.Windows.Forms.MessageBox.Show("Error creating aligned dimensions:\n{}".format(e))
