using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace CustomRevitCommand
{
    /// <summary>
    /// Continuous Dimension Chain Command
    /// Simple and reliable approach using point picking with temporary line visualization
    /// Continuous mode - draw multiple chains until ESC pressed
    /// 
    /// Workflow:
    /// 1. Pick start point
    /// 2. Pick end point with rubber band effect using ISelectionFilter
    /// 3. Create temporary visible line
    /// 4. Analyze and replace with dimension chain
    /// 5. Repeat until ESC pressed
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class DimensionChainCommand : IExternalCommand
    {
        #region Fields
        private Document _doc;
        private View _activeView;
        private UIDocument _uidoc;
        private UIApplication _uiapp;
        private int _chainCount = 0;
        #endregion

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            _uiapp = commandData.Application;
            _uidoc = _uiapp.ActiveUIDocument;
            _doc = _uidoc.Document;
            _activeView = _doc.ActiveView;

            try
            {
                if (!IsValidViewType(_activeView))
                {
                    message = "This command only works in plan, section, or elevation views.";
                    return Result.Cancelled;
                }

                return ExecuteContinuousDimensionChains();
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        #region Main Continuous Loop
        private Result ExecuteContinuousDimensionChains()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("Starting continuous dimension chain mode...");

                // Continuous mode - keep creating chains until ESC
                while (true)
                {
                    try
                    {
                        _chainCount++;
                        System.Diagnostics.Debug.WriteLine($"--- Starting chain #{_chainCount} ---");

                        // Create dimension chain with comprehensive approach
                        var result = CreateSingleDimensionChain();

                        if (result == Result.Cancelled)
                        {
                            // User pressed ESC - exit continuous mode
                            System.Diagnostics.Debug.WriteLine("User cancelled - exiting continuous mode");
                            break;
                        }
                        else if (result == Result.Failed)
                        {
                            // Error occurred, but continue
                            System.Diagnostics.Debug.WriteLine($"Chain #{_chainCount} failed, continuing...");
                            continue;
                        }

                        System.Diagnostics.Debug.WriteLine($"Chain #{_chainCount} completed successfully");
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        // User pressed ESC - exit continuous mode
                        System.Diagnostics.Debug.WriteLine("Operation cancelled by user - exiting");
                        break;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error in chain #{_chainCount}: {ex.Message}");
                        // Continue to next chain
                        continue;
                    }
                }

                var totalChains = Math.Max(0, _chainCount - 1);
                System.Diagnostics.Debug.WriteLine($"Continuous mode completed. Total chains created: {totalChains}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in continuous mode: {ex.Message}");
                return Result.Failed;
            }
        }

        private Result CreateSingleDimensionChain()
        {
            DetailLine tempLine = null;

            try
            {
                System.Diagnostics.Debug.WriteLine("Phase 1: Getting points from user...");

                // PHASE 1: Get two points from user with visual feedback
                var points = GetTwoPointsFromUser();
                if (points == null)
                {
                    return Result.Cancelled; // User cancelled
                }

                var startPoint = points.Item1;
                var endPoint = points.Item2;

                System.Diagnostics.Debug.WriteLine($"Points selected: {startPoint} to {endPoint}");

                // PHASE 2: Create temporary visible line for user feedback
                System.Diagnostics.Debug.WriteLine("Phase 2: Creating temporary line...");
                tempLine = CreateTemporaryLine(startPoint, endPoint);

                // PHASE 3: Analyze line and create dimension chain
                System.Diagnostics.Debug.WriteLine("Phase 3: Analyzing and creating dimension chain...");
                var result = AnalyzeAndCreateDimensionChain(startPoint, endPoint, tempLine);

                return result;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // Clean up temporary line if user cancels
                CleanupTemporaryLine(tempLine);
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating dimension chain: {ex.Message}");
                // Clean up temporary line on error
                CleanupTemporaryLine(tempLine);
                return Result.Failed;
            }
        }

        private Tuple<XYZ, XYZ> GetTwoPointsFromUser()
        {
            try
            {
                // Pick start point
                System.Diagnostics.Debug.WriteLine("Prompting for start point...");
                var startPoint = _uidoc.Selection.PickPoint($"Chain #{_chainCount}: Pick start point");

                // Pick end point
                System.Diagnostics.Debug.WriteLine("Prompting for end point...");
                var endPoint = _uidoc.Selection.PickPoint($"Chain #{_chainCount}: Pick end point");

                return new Tuple<XYZ, XYZ>(startPoint, endPoint);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("User cancelled point selection");
                return null;
            }
        }

        private DetailLine CreateTemporaryLine(XYZ startPoint, XYZ endPoint)
        {
            try
            {
                using (var trans = new Transaction(_doc, $"Create Temp Line #{_chainCount}"))
                {
                    trans.Start();

                    var line3D = Line.CreateBound(startPoint, endPoint);
                    var detailLine = _doc.Create.NewDetailCurve(_activeView, line3D) as DetailLine;

                    if (detailLine != null)
                    {
                        // Make line visible with bright red color and thick weight
                        var overrides = new OverrideGraphicSettings();
                        overrides.SetProjectionLineColor(new Color(255, 0, 0)); // Bright red
                        overrides.SetProjectionLineWeight(8); // Thick line
                        _activeView.SetElementOverrides(detailLine.Id, overrides);

                        System.Diagnostics.Debug.WriteLine($"Temporary line created with ID: {detailLine.Id}");
                    }

                    trans.Commit();
                    return detailLine;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating temporary line: {ex.Message}");
                return null;
            }
        }

        private void CleanupTemporaryLine(DetailLine tempLine)
        {
            if (tempLine == null) return;

            try
            {
                using (var trans = new Transaction(_doc, "Cleanup Temp Line"))
                {
                    trans.Start();
                    _doc.Delete(tempLine.Id);
                    trans.Commit();
                    System.Diagnostics.Debug.WriteLine($"Cleaned up temporary line: {tempLine.Id}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error cleaning up temporary line: {ex.Message}");
            }
        }

        private Result AnalyzeAndCreateDimensionChain(XYZ startPoint, XYZ endPoint, DetailLine tempLine)
        {
            try
            {
                // Determine dimension direction
                var dimensionDirection = DetermineDimensionDirection(startPoint, endPoint);
                System.Diagnostics.Debug.WriteLine($"Dimension direction: {dimensionDirection}");

                // Find intersected elements along the line
                var intersectedElements = FindElementsIntersectedByLine(startPoint, endPoint);
                System.Diagnostics.Debug.WriteLine($"Found {intersectedElements.Count} intersected elements");

                // Filter for suitable elements
                var filteredElements = FilterPerpendicularElements(intersectedElements, dimensionDirection);
                System.Diagnostics.Debug.WriteLine($"Filtered to {filteredElements.Count} suitable elements");

                if (filteredElements.Count < 2)
                {
                    System.Diagnostics.Debug.WriteLine($"Not enough elements ({filteredElements.Count}) for dimensioning");

                    // Not enough elements - keep the temporary line as a regular detail line
                    // User can see what they drew
                    return Result.Succeeded;
                }

                // Create dimension chain and remove temporary line
                return CreateDimensionChainAndCleanup(filteredElements, dimensionDirection, startPoint, endPoint, tempLine);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error analyzing line: {ex.Message}");
                return Result.Failed;
            }
        }

        private Result CreateDimensionChainAndCleanup(List<ElementCandidate> elements, XYZ dimensionDirection,
            XYZ startPoint, XYZ endPoint, DetailLine tempLine)
        {
            using (var trans = new Transaction(_doc, $"Create Dimension Chain #{_chainCount}"))
            {
                trans.Start();

                try
                {
                    // Create reference array
                    var refArray = new ReferenceArray();
                    foreach (var element in elements)
                    {
                        refArray.Append(element.Reference);
                    }

                    // Calculate dimension line position
                    var dimensionLine = CalculateDimensionLine(elements, dimensionDirection, startPoint, endPoint);

                    // Create dimension chain
                    var dimension = _doc.Create.NewDimension(_activeView, dimensionLine, refArray);

                    if (dimension != null)
                    {
                        // Success - remove temporary line and keep dimension
                        if (tempLine != null)
                        {
                            _doc.Delete(tempLine.Id);
                            System.Diagnostics.Debug.WriteLine("Temporary line replaced with dimension chain");
                        }

                        trans.Commit();
                        System.Diagnostics.Debug.WriteLine($"Chain #{_chainCount} created with {elements.Count} elements");
                        return Result.Succeeded;
                    }
                    else
                    {
                        // Failed to create dimension - keep temporary line
                        trans.RollBack();
                        System.Diagnostics.Debug.WriteLine("Failed to create dimension - keeping temporary line");
                        return Result.Failed;
                    }
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    System.Diagnostics.Debug.WriteLine($"Error creating dimension chain: {ex.Message}");
                    return Result.Failed;
                }
            }
        }
        #endregion

        #region Element Processing Logic
        private XYZ DetermineDimensionDirection(XYZ startPoint, XYZ endPoint)
        {
            var start2D = ProjectPointToView(startPoint);
            var end2D = ProjectPointToView(endPoint);

            var deltaX = Math.Abs(end2D.X - start2D.X);
            var deltaY = Math.Abs(end2D.Y - start2D.Y);

            if (deltaX > deltaY)
            {
                return new XYZ(1, 0, 0); // Horizontal dimension
            }
            else
            {
                return new XYZ(0, 1, 0); // Vertical dimension
            }
        }

        private List<ElementCandidate> FindElementsIntersectedByLine(XYZ startPoint, XYZ endPoint)
        {
            var intersectedElements = new List<ElementCandidate>();

            try
            {
                var allElements = GetAllDimensionableElements();
                var lineStart2D = ProjectPointToView(startPoint);
                var lineEnd2D = ProjectPointToView(endPoint);

                System.Diagnostics.Debug.WriteLine($"Checking intersection with {allElements.Count} elements");

                foreach (var element in allElements)
                {
                    if (DoesElementIntersectLine(element, lineStart2D, lineEnd2D))
                    {
                        var candidate = CreateElementCandidate(element);
                        if (candidate != null && candidate.IsValid)
                        {
                            intersectedElements.Add(candidate);
                            System.Diagnostics.Debug.WriteLine($"Added element: {element.Category?.Name} - {element.Id}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error finding intersected elements: {ex.Message}");
            }

            return intersectedElements;
        }

        private List<ElementCandidate> FilterPerpendicularElements(List<ElementCandidate> intersectedElements, XYZ dimensionDirection)
        {
            var filteredElements = new List<ElementCandidate>();
            bool isHorizontalDimension = Math.Abs(dimensionDirection.X) > Math.Abs(dimensionDirection.Y);

            try
            {
                foreach (var element in intersectedElements)
                {
                    if (IsElementPerpendicularToDimension(element, isHorizontalDimension))
                    {
                        filteredElements.Add(element);
                    }
                }

                // Sort elements by position along dimension direction
                filteredElements.Sort((a, b) =>
                    a.CenterPoint.DotProduct(dimensionDirection).CompareTo(
                    b.CenterPoint.DotProduct(dimensionDirection)));

                System.Diagnostics.Debug.WriteLine($"After filtering: {filteredElements.Count} perpendicular elements");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error filtering elements: {ex.Message}");
            }

            return filteredElements;
        }

        private bool DoesElementIntersectLine(Element element, XYZ lineStart2D, XYZ lineEnd2D)
        {
            try
            {
                if (element is Grid grid && grid.Curve is Line gridLine)
                {
                    var gridStart2D = ProjectPointToView(gridLine.GetEndPoint(0));
                    var gridEnd2D = ProjectPointToView(gridLine.GetEndPoint(1));
                    return DoLinesIntersect2D(lineStart2D, lineEnd2D, gridStart2D, gridEnd2D);
                }
                else if (element is Level level)
                {
                    if (_activeView.ViewType == ViewType.Section || _activeView.ViewType == ViewType.Elevation)
                    {
                        var levelPoint2D = ProjectPointToView(new XYZ(0, 0, level.Elevation));
                        var levelStart2D = new XYZ(levelPoint2D.X - 1000, levelPoint2D.Y, 0);
                        var levelEnd2D = new XYZ(levelPoint2D.X + 1000, levelPoint2D.Y, 0);
                        return DoLinesIntersect2D(lineStart2D, lineEnd2D, levelStart2D, levelEnd2D);
                    }
                }
                else if (element.Location is LocationCurve locationCurve)
                {
                    var curve = locationCurve.Curve;
                    var elemStart2D = ProjectPointToView(curve.GetEndPoint(0));
                    var elemEnd2D = ProjectPointToView(curve.GetEndPoint(1));
                    return DoLinesIntersect2D(lineStart2D, lineEnd2D, elemStart2D, elemEnd2D);
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking intersection for element {element.Id}: {ex.Message}");
                return false;
            }
        }

        private bool DoLinesIntersect2D(XYZ line1Start, XYZ line1End, XYZ line2Start, XYZ line2End)
        {
            var x1 = line1Start.X; var y1 = line1Start.Y;
            var x2 = line1End.X; var y2 = line1End.Y;
            var x3 = line2Start.X; var y3 = line2Start.Y;
            var x4 = line2End.X; var y4 = line2End.Y;

            var denom = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
            if (Math.Abs(denom) < 1e-10)
            {
                return AreCollinearAndOverlapping(line1Start, line1End, line2Start, line2End);
            }

            var t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / denom;
            var u = -((x1 - x2) * (y1 - y3) - (y1 - y2) * (x1 - x3)) / denom;

            return (t >= 0 && t <= 1) && (u >= 0 && u <= 1);
        }

        private bool AreCollinearAndOverlapping(XYZ line1Start, XYZ line1End, XYZ line2Start, XYZ line2End)
        {
            const double tolerance = 0.1;

            var area1 = TriangleArea(line1Start, line1End, line2Start);
            var area2 = TriangleArea(line1Start, line1End, line2End);

            if (area1 > tolerance || area2 > tolerance) return false;

            var lineDir = (line1End - line1Start).Normalize();
            var proj1Start = 0.0;
            var proj1End = (line1End - line1Start).GetLength();
            var proj2Start = (line2Start - line1Start).DotProduct(lineDir);
            var proj2End = (line2End - line1Start).DotProduct(lineDir);

            if (proj2Start > proj2End)
            {
                var temp = proj2Start;
                proj2Start = proj2End;
                proj2End = temp;
            }

            return !(proj2End < proj1Start || proj2Start > proj1End);
        }

        private double TriangleArea(XYZ p1, XYZ p2, XYZ p3)
        {
            return Math.Abs((p1.X * (p2.Y - p3.Y) + p2.X * (p3.Y - p1.Y) + p3.X * (p1.Y - p2.Y)) / 2.0);
        }

        private bool IsElementPerpendicularToDimension(ElementCandidate element, bool isHorizontalDimension)
        {
            try
            {
                // Always include grids and levels
                if (element.Element is Grid || element.Element is Level)
                    return true;

                // For linear elements, check perpendicularity
                if (element.Element.Location is LocationCurve locationCurve)
                {
                    var curve = locationCurve.Curve;
                    var elementDirection3D = (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize();
                    var elementDirection2D = ProjectDirectionToView(elementDirection3D);

                    if (isHorizontalDimension)
                    {
                        // For horizontal dimensions, keep vertical elements
                        var dotProduct = Math.Abs(elementDirection2D.DotProduct(new XYZ(1, 0, 0)));
                        return dotProduct < 0.5; // Less than 60 degrees from vertical
                    }
                    else
                    {
                        // For vertical dimensions, keep horizontal elements
                        var dotProduct = Math.Abs(elementDirection2D.DotProduct(new XYZ(0, 1, 0)));
                        return dotProduct < 0.5; // Less than 60 degrees from horizontal
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking perpendicularity: {ex.Message}");
                return false;
            }
        }

        private Line CalculateDimensionLine(List<ElementCandidate> elements, XYZ dimensionDirection, XYZ startPoint, XYZ endPoint)
        {
            try
            {
                // Calculate group center
                var groupCenter = XYZ.Zero;
                foreach (var element in elements)
                {
                    groupCenter += element.CenterPoint;
                }
                groupCenter = groupCenter / elements.Count;

                // Calculate perpendicular direction
                var perpDirection = new XYZ(-dimensionDirection.Y, dimensionDirection.X, 0).Normalize();

                // Calculate offset from line to dimension
                var lineCenter = (ProjectPointToView(startPoint) + ProjectPointToView(endPoint)) / 2;
                var offsetVector = lineCenter - groupCenter;
                var offsetDistance = offsetVector.DotProduct(perpDirection);

                // Ensure minimum offset for visibility
                if (Math.Abs(offsetDistance) < 3.0)
                {
                    offsetDistance = offsetDistance >= 0 ? 5.0 : -5.0;
                }

                var dimLineCenter = groupCenter + perpDirection * offsetDistance;

                // Calculate extents
                var positions = elements.Select(e => e.CenterPoint.DotProduct(dimensionDirection)).ToList();
                var minPos = positions.Min();
                var maxPos = positions.Max();
                var extension = Math.Max((maxPos - minPos) * 0.1, 3.0);

                var dimStart2D = dimLineCenter + dimensionDirection * (minPos - extension);
                var dimEnd2D = dimLineCenter + dimensionDirection * (maxPos + extension);

                var dimStart3D = ConvertViewPointTo3D(dimStart2D);
                var dimEnd3D = ConvertViewPointTo3D(dimEnd2D);

                return Line.CreateBound(dimStart3D, dimEnd3D);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error calculating dimension line: {ex.Message}");
                throw;
            }
        }
        #endregion

        #region Helper Methods
        private bool IsValidViewType(View view)
        {
            switch (view.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.AreaPlan:
                case ViewType.Section:
                case ViewType.Elevation:
                    return true;
                default:
                    return false;
            }
        }

        private List<Element> GetAllDimensionableElements()
        {
            var elements = new List<Element>();

            try
            {
                // Get elements visible in current view
                var viewCollector = new FilteredElementCollector(_doc, _activeView.Id);
                var viewElements = viewCollector.ToElements();

                // Get all grids and levels (they might not be in view filter)
                var gridCollector = new FilteredElementCollector(_doc).OfClass(typeof(Grid));
                var levelCollector = new FilteredElementCollector(_doc).OfClass(typeof(Level));

                // Combine all elements and remove duplicates
                var allElements = viewElements
                    .Concat(gridCollector.ToElements())
                    .Concat(levelCollector.ToElements())
                    .GroupBy(e => e.Id)
                    .Select(g => g.First());

                foreach (var element in allElements)
                {
                    if (IsDimensionableElement(element))
                    {
                        elements.Add(element);
                    }
                }

                System.Diagnostics.Debug.WriteLine($"Found {elements.Count} dimensionable elements");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting elements: {ex.Message}");
            }

            return elements;
        }

        private bool IsDimensionableElement(Element element)
        {
            if (element == null || element.Category == null) return false;

            // Must have location curve or be grid/level
            if (!(element.Location is LocationCurve) && !(element is Grid) && !(element is Level))
                return false;

            // Exclude certain categories
            string categoryName = element.Category.Name.ToLower();
            if (categoryName.Contains("fitting") ||
                categoryName.Contains("accessory") ||
                categoryName.Contains("insulation") ||
                categoryName.Contains("tag"))
                return false;

            return true;
        }

        private ElementCandidate CreateElementCandidate(Element element)
        {
            try
            {
                if (!IsDimensionableElement(element)) return null;

                var candidate = new ElementCandidate
                {
                    Element = element,
                    Reference = GetBestReferenceForElement(element),
                    CenterPoint = GetElementCenterPoint(element),
                    IsValid = true
                };

                // Validate the reference
                if (candidate.Reference == null)
                {
                    candidate.IsValid = false;
                }

                return candidate;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating candidate for element {element.Id}: {ex.Message}");
                return null;
            }
        }

        private Reference GetBestReferenceForElement(Element element)
        {
            try
            {
                if (element is FamilyInstance familyInstance)
                {
                    var centerlineRefs = familyInstance.GetReferences(FamilyInstanceReferenceType.CenterLeftRight);
                    if (centerlineRefs?.Count > 0) return centerlineRefs.First();

                    var elevationRefs = familyInstance.GetReferences(FamilyInstanceReferenceType.CenterElevation);
                    if (elevationRefs?.Count > 0) return elevationRefs.First();
                }

                return new Reference(element);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting reference for element {element.Id}: {ex.Message}");
                return new Reference(element);
            }
        }

        private XYZ GetElementCenterPoint(Element element)
        {
            try
            {
                if (element is Grid grid && grid.Curve is Line gridLine)
                {
                    var center3D = (gridLine.GetEndPoint(0) + gridLine.GetEndPoint(1)) / 2;
                    return ProjectPointToView(center3D);
                }
                else if (element is Level level)
                {
                    if (_activeView.ViewType == ViewType.Section || _activeView.ViewType == ViewType.Elevation)
                    {
                        var levelPoint3D = new XYZ(0, 0, level.Elevation);
                        return ProjectPointToView(levelPoint3D);
                    }
                }
                else if (element.Location is LocationCurve locationCurve)
                {
                    var curve = locationCurve.Curve;
                    var center3D = (curve.GetEndPoint(0) + curve.GetEndPoint(1)) / 2;
                    return ProjectPointToView(center3D);
                }

                return XYZ.Zero;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting center point for element {element.Id}: {ex.Message}");
                return XYZ.Zero;
            }
        }

        private XYZ ProjectPointToView(XYZ point3D)
        {
            try
            {
                switch (_activeView.ViewType)
                {
                    case ViewType.FloorPlan:
                    case ViewType.CeilingPlan:
                    case ViewType.AreaPlan:
                        return new XYZ(point3D.X, point3D.Y, 0);
                    case ViewType.Section:
                    case ViewType.Elevation:
                        var viewOrigin = _activeView.Origin;
                        var viewRight = _activeView.RightDirection.Normalize();
                        var viewUp = _activeView.UpDirection.Normalize();
                        var relativePoint = point3D - viewOrigin;
                        var x = relativePoint.DotProduct(viewRight);
                        var y = relativePoint.DotProduct(viewUp);
                        return new XYZ(x, y, 0);
                    default:
                        return new XYZ(point3D.X, point3D.Y, 0);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error projecting point to view: {ex.Message}");
                return point3D;
            }
        }

        private XYZ ProjectDirectionToView(XYZ direction3D)
        {
            try
            {
                switch (_activeView.ViewType)
                {
                    case ViewType.FloorPlan:
                    case ViewType.CeilingPlan:
                    case ViewType.AreaPlan:
                        var dir2D = new XYZ(direction3D.X, direction3D.Y, 0);
                        return dir2D.GetLength() > 0.001 ? dir2D.Normalize() : new XYZ(1, 0, 0);
                    case ViewType.Section:
                    case ViewType.Elevation:
                        var viewRight = _activeView.RightDirection.Normalize();
                        var viewUp = _activeView.UpDirection.Normalize();
                        var x = direction3D.DotProduct(viewRight);
                        var y = direction3D.DotProduct(viewUp);
                        var projectedDir = new XYZ(x, y, 0);
                        return projectedDir.GetLength() > 0.001 ? projectedDir.Normalize() : new XYZ(1, 0, 0);
                    default:
                        var defaultDir = new XYZ(direction3D.X, direction3D.Y, 0);
                        return defaultDir.GetLength() > 0.001 ? defaultDir.Normalize() : new XYZ(1, 0, 0);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error projecting direction to view: {ex.Message}");
                return new XYZ(1, 0, 0);
            }
        }

        private XYZ ConvertViewPointTo3D(XYZ point2D)
        {
            try
            {
                switch (_activeView.ViewType)
                {
                    case ViewType.FloorPlan:
                    case ViewType.CeilingPlan:
                    case ViewType.AreaPlan:
                        var level = _doc.GetElement(_activeView.LevelId) as Level;
                        var elevation = level?.Elevation ?? 0;
                        return new XYZ(point2D.X, point2D.Y, elevation);
                    case ViewType.Section:
                    case ViewType.Elevation:
                        var viewOrigin = _activeView.Origin;
                        var viewRight = _activeView.RightDirection.Normalize();
                        var viewUp = _activeView.UpDirection.Normalize();
                        return viewOrigin + viewRight * point2D.X + viewUp * point2D.Y;
                    default:
                        return new XYZ(point2D.X, point2D.Y, 0);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error converting view point to 3D: {ex.Message}");
                return point2D;
            }
        }
        #endregion
    }

    #region Supporting Classes
    public class ElementCandidate
    {
        public Element Element { get; set; }
        public Reference Reference { get; set; }
        public XYZ CenterPoint { get; set; }
        public bool IsValid { get; set; }
    }
    #endregion
}