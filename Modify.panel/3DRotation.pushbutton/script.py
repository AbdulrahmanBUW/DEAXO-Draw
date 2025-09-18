# -*- coding: utf-8 -*-
"""
AutoSection (pyRevit) - selection based, pyrevit.forms UI version (no XAML)
Place as: <your-extensions>/AutoSection.extension/AutoSection.pushbutton/script.py
"""

from Autodesk.Revit.DB import *
from Autodesk.Revit.UI.Selection import ObjectType
from pyrevit import revit, forms, script

uidoc = revit.uidoc
doc = revit.doc

# -----------------------
# Helper UI & utilities
# -----------------------

def get_section_view_family_types():
    vfts = []
    for vft in FilteredElementCollector(doc).OfClass(ViewFamilyType).ToElements():
        try:
            if vft.ViewFamily == ViewFamily.Section:
                vfts.append(vft)
        except Exception:
            continue
    return vfts

def show_options_dialog(vft_names):
    """
    Ask user for options using pyrevit.forms (fallback UI).
    Returns dict or None if cancelled.
    """
    fields = {
        'offset_mm': forms.TextBox('Offset (mm)', '300'),
        'tolerance_m': forms.TextBox('Tolerance (m)', '0.5'),
        'include_links': forms.CheckBox('Include linked models (approx)', True),
        'section_type': forms.ComboBox('Section type', vft_names, vft_names[0] if vft_names else None)
    }
    res = forms.ask_for_one(fields, title='Auto Section - Options')
    if not res:
        return None
    try:
        return {
            'offset_m': float(res['offset_mm']) / 1000.0,
            'tolerance_m': float(res['tolerance_m']),
            'include_links': bool(res['include_links']),
            'view_family_type_name': res['section_type']
        }
    except Exception:
        forms.alert('Invalid numeric values entered. Aborting.', title='Auto Section')
        return None

def element_direction(elem):
    """Return the direction vector for the element (local axis)."""
    try:
        if elem.Category and elem.Category.Id.IntegerValue == BuiltInCategory.OST_Walls:
            loc = elem.Location
            if isinstance(loc, LocationCurve):
                c = loc.Curve
                return (c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize()
    except Exception:
        pass
    try:
        # Grids expose .Curve
        if elem.Category and elem.Category.Id.IntegerValue == BuiltInCategory.OST_Grids:
            c = elem.Curve
            return (c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize()
    except Exception:
        pass
    # fallback based on bounding box principal axis
    try:
        bb = elem.get_BoundingBox(None)
        if bb:
            dx = abs(bb.Max.X - bb.Min.X)
            dy = abs(bb.Max.Y - bb.Min.Y)
            dz = abs(bb.Max.Z - bb.Min.Z)
            if dx >= dy and dx >= dz:
                return XYZ(1,0,0)
            elif dy >= dx and dy >= dz:
                return XYZ(0,1,0)
            else:
                return XYZ(0,0,1)
    except Exception:
        pass
    return XYZ(1,0,0)

def get_center_point_for_element(elem):
    """Return a representative center point (curve midpoint or bbox center)."""
    try:
        if hasattr(elem, 'Location') and isinstance(elem.Location, LocationCurve):
            c = elem.Location.Curve
            return (c.GetEndPoint(0) + c.GetEndPoint(1)) / 2.0
    except Exception:
        pass
    try:
        bb = elem.get_BoundingBox(doc.ActiveView)
        if bb:
            return (bb.Min + bb.Max) / 2.0
    except Exception:
        pass
    # final fallback
    return XYZ(0,0,0)

def find_nearby_elements(center_pt, dir_vec, half_length, include_links=False):
    """
    Collect elements in the active view whose bounding boxes overlap a big slab around center_pt.
    This is a simple heuristic to gather geometry to compute section extents.
    """
    elems = []
    bb_min = XYZ(center_pt.X - half_length, center_pt.Y - half_length, center_pt.Z - 10.0)
    bb_max = XYZ(center_pt.X + half_length, center_pt.Y + half_length, center_pt.Z + 10.0)
    collector = FilteredElementCollector(doc, doc.ActiveView.Id).WhereElementIsNotElementType()
    for el in collector:
        try:
            el_bb = el.get_BoundingBox(doc.ActiveView)
            if not el_bb:
                continue
            if not (el_bb.Max.X < bb_min.X or el_bb.Min.X > bb_max.X or el_bb.Max.Y < bb_min.Y or el_bb.Min.Y > bb_max.Y):
                elems.append(el)
        except Exception:
            continue
    if include_links:
        # Include RevitLinkInstance as approximate geometry containers
        try:
            links = FilteredElementCollector(doc).OfClass(RevitLinkInstance).ToElements()
        except Exception:
            links = []
        for li in links:
            try:
                lb = li.get_BoundingBox(doc.ActiveView)
                if not lb:
                    continue
                if not (lb.Max.X < bb_min.X or lb.Min.X > bb_max.X or lb.Max.Y < bb_min.Y or lb.Min.Y > bb_max.Y):
                    elems.append(li)
            except Exception:
                continue
    return elems

def compute_section_outline(center_pt, dir_vec, elems, offset_m):
    """
    Compute world-space min/max corner points for an outline that covers `elems` projected along dir_vec.
    The algorithm projects bbox corners on dir_vec and perpendicular, finds min/max and expands by offset_m.
    """
    perp = XYZ(-dir_vec.Y, dir_vec.X, 0)
    try:
        perp = perp.Normalize()
    except Exception:
        perp = XYZ(0,0,1)
    projections = []
    for el in elems:
        try:
            bb = el.get_BoundingBox(doc.ActiveView)
            if bb is None:
                continue
            # use 4 corners in XY plane (Z set to bb.Min.Z)
            corners = [
                XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z),
                XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z),
                XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z)
            ]
            for c in corners:
                projections.append((c.DotProduct(dir_vec), c.DotProduct(perp), c))
        except Exception:
            continue
    if not projections:
        # tiny default box
        a = center_pt - XYZ(1,1,1)
        b = center_pt + XYZ(1,1,1)
        return a, b
    min_along = min([p[0] for p in projections])
    max_along = max([p[0] for p in projections])
    min_perp = min([p[1] for p in projections])
    max_perp = max([p[1] for p in projections])
    min_perp -= offset_m
    max_perp += offset_m
    # build world-space using dir_vec and perp using center_pt as origin
    origin_along = center_pt.DotProduct(dir_vec)
    origin_perp = center_pt.DotProduct(perp)
    origin = dir_vec.Multiply(origin_along) + perp.Multiply(origin_perp)
    pt_min = dir_vec.Multiply(min_along) + perp.Multiply(min_perp)
    pt_max = dir_vec.Multiply(max_along) + perp.Multiply(max_perp)
    world_min = origin + (pt_min - origin)
    world_max = origin + (pt_max - origin)
    # compute z extents from projections
    zs = []
    for _,_,c in projections:
        zs.append(c.Z)
    if zs:
        zmin = min(zs) - 1.0
        zmax = max(zs) + 1.0
    else:
        zmin = center_pt.Z - 5.0
        zmax = center_pt.Z + 5.0
    world_min = XYZ(world_min.X, world_min.Y, zmin)
    world_max = XYZ(world_max.X, world_max.Y, zmax)
    return world_min, world_max

def section_exists_nearby(center_pt, tolerance_m):
    """Return True if any existing section view crop center is within tolerance_m (meters)."""
    try:
        tol = float(tolerance_m)
    except Exception:
        tol = 0.5
    for v in FilteredElementCollector(doc).OfClass(ViewSection).ToElements():
        try:
            bb = v.CropBox
            if not bb:
                continue
            v_center = (bb.Min + bb.Max) / 2.0
            if (v_center - center_pt).GetLength() <= tol:
                return True
        except Exception:
            continue
    return False

# -----------------------
# Main flow
# -----------------------

# 1) selection
sel_ids = list(uidoc.Selection.GetElementIds())
if len(sel_ids) != 1:
    forms.alert('Please select exactly one element (wall / grid / line / family) before running the tool.', title='Auto Section')
    script.exit()
sel_elem = doc.GetElement(sel_ids[0])

# 2) ui options
vfts = get_section_view_family_types()
vft_names = [vf.Name for vf in vfts]
options = show_options_dialog(vft_names)
if not options:
    script.exit()

# map chosen vft
vft_choice = None
chosen_name = options.get('view_family_type_name')
for vf in vfts:
    if vf.Name == chosen_name:
        vft_choice = vf
        break

offset_m = options['offset_m']
tolerance_m = options['tolerance_m']
include_links = options['include_links']

# 3) compute orientation and center
dir_vec = element_direction(sel_elem)
if dir_vec is None:
    forms.alert('Could not determine element direction. Aborting.', title='Auto Section')
    script.exit()

center_pt = get_center_point_for_element(sel_elem)

# 4) check existing nearby sections
if section_exists_nearby(center_pt, tolerance_m):
    forms.alert('A section view already exists near this location (within {0} m). Skipping.'.format(tolerance_m), title='Auto Section')
    script.exit()

# 5) gather nearby elements and compute outline
half_length = 50.0   # search ±50 meters
elems = find_nearby_elements(center_pt, dir_vec, half_length, include_links)
min_pt, max_pt = compute_section_outline(center_pt, dir_vec, elems, offset_m)

# 6) create section view
try:
    tr = Transaction(doc, 'Create Auto Section')
    tr.Start()
    if vft_choice is None:
        vf_types = get_section_view_family_types()
        if not vf_types:
            raise Exception('No Section view family types found in project')
        vft_id = vf_types[0].Id
    else:
        vft_id = vft_choice.Id

    outline = Outline(min_pt, max_pt)
    new_view = ViewSection.CreateSection(doc, vft_id, outline)

    # build view name using element and nearest level
    level_name = 'L0'
    try:
        lvl = None
        if hasattr(sel_elem, 'LevelId') and sel_elem.LevelId != ElementId.InvalidElementId:
            lvl = doc.GetElement(sel_elem.LevelId)
        if lvl is None:
            levels = FilteredElementCollector(doc).OfClass(Level).ToElements()
            if levels:
                best = None
                bestd = 1e9
                for L in levels:
                    d = abs(center_pt.Z - L.Elevation)
                    if d < bestd:
                        bestd = d
                        best = L
                if best:
                    lvl = best
        if lvl:
            level_name = lvl.Name
    except Exception:
        pass

    try:
        # element name: prefer element.Name, else family name, else category
        el_name = sel_elem.Name if hasattr(sel_elem, 'Name') and sel_elem.Name else (sel_elem.Symbol.FamilyName if hasattr(sel_elem, 'Symbol') else (sel_elem.Category.Name if sel_elem.Category else 'Element'))
    except Exception:
        el_name = 'Element'
    view_name = '{}_{}'.format(el_name.replace(' ', '_'), level_name)
    new_view.Name = view_name

    # apply cropbox (BoundingBoxXYZ) to new view
    try:
        bbxyz = BoundingBoxXYZ()
        bbxyz.Min = min_pt
        bbxyz.Max = max_pt
        new_view.CropBox = bbxyz
        new_view.CropBoxActive = True
        new_view.CropBoxVisible = True
    except Exception:
        pass

    tr.Commit()
    forms.alert('Section view created: {}'.format(view_name), title='Auto Section')

except Exception as ex:
    try:
        if tr and tr.HasStarted():
            tr.RollBack()
    except Exception:
        pass
    forms.alert('Failed to create section: {}'.format(ex), title='Auto Section')
