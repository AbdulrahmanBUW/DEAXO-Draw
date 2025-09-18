using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

// Alias to resolve namespace conflicts
using RevitGrid = Autodesk.Revit.DB.Grid;

namespace CustomRevitCommand
{
    // External Command class - Auto Dimension (Multi-View)
    [Transaction(TransactionMode.Manual)]
    public class AutoDimensionCommand : IExternalCommand
    {
        private const double PARALLEL_TOLERANCE = 0.05; // ~3 degrees for 2D
        private const double PERPENDICULAR_TOLERANCE = 0.1; // Tolerance for perpendicular detection
        private const double DIMENSION_OFFSET = 1.64; // feet (0.5 meters)

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // STEP 1: Check if any elements are selected
                ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();

                if (selectedIds.Count == 0)
                {
                    TaskDialog.Show("Error", "Please select at least one element before running the Auto-Dimension tool.");
                    return Result.Cancelled;
                }

                // STEP 2: Show the WPF view selection dialog - PASS CURRENT VIEW
                ViewSelectionWindow viewSelectionWindow = new ViewSelectionWindow(doc, uidoc.ActiveView);
                bool? dialogResult = viewSelectionWindow.ShowDialog();

                // STEP 3: Check if user clicked OK and selected views
                if (dialogResult != true || viewSelectionWindow.SelectedViews.Count == 0)
                {
                    return Result.Cancelled; // User clicked Cancel or didn't select any views
                }

                // STEP 4: Get the selected views from the dialog
                List<View> selectedViews = viewSelectionWindow.SelectedViews;

                // STEP 5: Process each selected view
                int totalChainsCreated = 0;
                List<string> processedViews = new List<string>();
                List<string> failedViews = new List<string>();
                View originalActiveView = uidoc.ActiveView; // Remember the original active view

                using (TransactionGroup transGroup = new TransactionGroup(doc, "Auto-Dimension Multiple Views"))
                {
                    transGroup.Start();

                    foreach (View view in selectedViews)
                    {
                        try
                        {
                            // STEP 6: Change view OUTSIDE of transaction (this is the key fix!)
                            uidoc.ActiveView = view;

                            // STEP 7: Now start transaction for this view
                            using (Transaction trans = new Transaction(doc, $"Auto-Dimension - {view.Name}"))
                            {
                                trans.Start();

                                // STEP 8: Apply your existing dimensioning logic to this view
                                int chainsCreated = ProcessViewForDimensioning(doc, view, selectedIds);

                                if (chainsCreated > 0)
                                {
                                    totalChainsCreated += chainsCreated;
                                    processedViews.Add($"✓ {view.Name}: {chainsCreated} chain(s)");
                                    trans.Commit();
                                }
                                else
                                {
                                    failedViews.Add($"⚠ {view.Name}: No dimensions created");
                                    trans.RollBack();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            failedViews.Add($"✗ {view.Name}: Error - {ex.Message}");
                            System.Diagnostics.Debug.WriteLine($"Error processing view {view.Name}: {ex.Message}");
                        }
                    }

                    // STEP 9: Restore original active view OUTSIDE of individual transactions
                    try
                    {
                        uidoc.ActiveView = originalActiveView;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Could not restore original view: {ex.Message}");
                        // This is not critical, just log it
                    }

                    transGroup.Assimilate();
                }

                // STEP 10: Show results summary to user
                ShowResultsSummary(totalChainsCreated, processedViews, failedViews);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Exception: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// Process a single view for dimensioning (view is already active when this is called)
        /// </summary>
        private int ProcessViewForDimensioning(Document doc, View view, ICollection<ElementId> selectedIds)
        {
            try
            {
                // STEP 1: Collect all items (elements, grids, levels)
                List<ProjectedItem> projectedItems = CollectAllItems(doc, view, selectedIds);

                if (projectedItems.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"No valid items found in view: {view.Name}");
                    return 0;
                }

                // STEP 2: Group by parallel directions
                var parallelGroups = GroupByParallelDirections(projectedItems);

                // STEP 3: Create combined dimension groups
                var dimensionGroups = CreateCombinedDimensionGroups(parallelGroups);

                // STEP 4: Create dimension chains
                int chainsCreated = 0;
                List<Dimension> createdDimensions = new List<Dimension>();

                foreach (var group in dimensionGroups)
                {
                    if (group.Value.Count >= 2)
                    {
                        Dimension newDimension = CreateDimensionChain(doc, view, group.Key, group.Value);
                        if (newDimension != null)
                        {
                            chainsCreated++;
                            createdDimensions.Add(newDimension);
                        }
                    }
                }

                // STEP 5: Move dimensions
                if (createdDimensions.Count > 0)
                {
                    MoveDimensionsLeft(doc, createdDimensions, 0.0328084); // 10mm in feet
                }

                return chainsCreated;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error processing view {view.Name}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Show a summary of results to the user
        /// </summary>
        private void ShowResultsSummary(int totalChains, List<string> processedViews, List<string> failedViews)
        {
            string summary = $"Auto-Dimension Results:\n\n";
            summary += $"Total dimension chains created: {totalChains}\n\n";

            if (processedViews.Count > 0)
            {
                summary += "Successfully processed views:\n";
                foreach (string view in processedViews)
                {
                    summary += $"{view}\n";
                }
                summary += "\n";
            }

            if (failedViews.Count > 0)
            {
                summary += "Views with issues:\n";
                foreach (string view in failedViews)
                {
                    summary += $"{view}\n";
                }
            }

            TaskDialog resultDialog = new TaskDialog("Auto-Dimension Results");
            resultDialog.MainContent = summary;
            resultDialog.CommonButtons = TaskDialogCommonButtons.Ok;

            if (totalChains > 0)
            {
                resultDialog.MainIcon = TaskDialogIcon.TaskDialogIconInformation;
            }
            else
            {
                resultDialog.MainIcon = TaskDialogIcon.TaskDialogIconWarning;
            }

            resultDialog.Show();
        }

        // STEP 1: Collect all items (elements, grids, levels, perpendicular elements)
        private List<ProjectedItem> CollectAllItems(Document doc, View view, ICollection<ElementId> selectedIds)
        {
            List<ProjectedItem> projectedItems = new List<ProjectedItem>();

            // Get visible elements in view
            FilteredElementCollector collector = new FilteredElementCollector(doc, view.Id);

            foreach (Element element in collector.ToElements())
            {
                ProjectedItem projectedItem = null;

                if (element is RevitGrid grid)
                {
                    projectedItem = ProcessGrid(grid, view);
                }
                else if (element is Level level && (view.ViewType == ViewType.Section || view.ViewType == ViewType.Elevation))
                {
                    projectedItem = ProcessLevel(level, view);
                }
                else if (HasCenterline(element) && selectedIds.Contains(element.Id))
                {
                    // First try to process as regular linear element
                    projectedItem = ProcessElement(element, view);

                    // If it fails as linear element, try as perpendicular element
                    if (projectedItem == null)
                    {
                        projectedItem = ProcessPerpendicularElement(element, view);
                    }
                }

                if (projectedItem != null)
                {
                    projectedItems.Add(projectedItem);
                }
            }

            // For layouts, also get ALL grids from project (not just visible ones)
            if (view.ViewType == ViewType.FloorPlan || view.ViewType == ViewType.CeilingPlan || view.ViewType == ViewType.AreaPlan)
            {
                FilteredElementCollector allGrids = new FilteredElementCollector(doc).OfClass(typeof(RevitGrid));

                foreach (RevitGrid grid in allGrids.Cast<RevitGrid>())
                {
                    // Skip if already processed
                    if (projectedItems.Any(p => p.Element.Id == grid.Id)) continue;

                    ProjectedItem projectedGrid = ProcessGrid(grid, view);
                    if (projectedGrid != null)
                    {
                        projectedItems.Add(projectedGrid);
                    }
                }
            }

            return projectedItems;
        }

        // Process elements that are perpendicular to the view surface
        private ProjectedItem ProcessPerpendicularElement(Element element, View view)
        {
            try
            {
                if (!(element.Location is LocationCurve locationCurve)) return null;

                Curve curve = locationCurve.Curve;
                XYZ start3D = curve.GetEndPoint(0);
                XYZ end3D = curve.GetEndPoint(1);
                XYZ elementDirection3D = (end3D - start3D).Normalize();

                // Get view plane normal
                XYZ viewNormal = GetViewPlaneNormal(view);

                // Check if element is perpendicular to view plane
                double dotProduct = Math.Abs(elementDirection3D.DotProduct(viewNormal));
                bool isPerpendicular = Math.Abs(dotProduct - 1.0) < PERPENDICULAR_TOLERANCE;

                if (!isPerpendicular) return null;

                System.Diagnostics.Debug.WriteLine($"Found perpendicular element: {element.Id} - {element.Category?.Name}");

                // Project element center to view surface (will be a single point)
                XYZ center3D = (start3D + end3D) / 2;
                XYZ projectedPoint = ProjectPointToViewSurface(center3D, view);

                Reference centerlineRef = GetCenterlineReference(element);

                return new ProjectedItem
                {
                    Element = element,
                    GeometricReference = centerlineRef,
                    ProjectedDirection = null, // No direction for point elements
                    ProjectedPoint = projectedPoint,
                    ItemType = "PerpendicularElement",
                    IsSelected = true,
                    IsPointElement = true
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error processing perpendicular element: {ex.Message}");
                return null;
            }
        }

        // Get view plane normal vector
        private XYZ GetViewPlaneNormal(View view)
        {
            switch (view.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.AreaPlan:
                    return XYZ.BasisZ; // Vertical elements are perpendicular to plan views

                case ViewType.Section:
                case ViewType.Elevation:
                    // For sections/elevations, the view direction is the normal to the cutting plane
                    return view.ViewDirection.Normalize();

                default:
                    return XYZ.BasisZ;
            }
        }

        private ProjectedItem ProcessGrid(RevitGrid grid, View view)
        {
            try
            {
                // For sections/elevations: Get view-specific geometry
                if (view.ViewType == ViewType.Section || view.ViewType == ViewType.Elevation)
                {
                    return ProcessGridInSectionView(grid, view);
                }
                else
                {
                    // For layouts: Use standard 3D projection
                    return ProcessGridInLayoutView(grid, view);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error processing grid {grid.Name}: {ex.Message}");
                return null;
            }
        }

        private ProjectedItem ProcessGridInSectionView(RevitGrid grid, View view)
        {
            try
            {
                // Get geometry as it actually appears in this view
                Options geometryOptions = new Options();
                geometryOptions.View = view;
                geometryOptions.IncludeNonVisibleObjects = false;

                GeometryElement geometryElement = grid.get_Geometry(geometryOptions);
                if (geometryElement == null) return null;

                Line gridLineInView = null;

                // Extract the line geometry from the grid as it appears in the view
                foreach (GeometryObject geoObj in geometryElement)
                {
                    if (geoObj is Line line)
                    {
                        gridLineInView = line;
                        break;
                    }
                    else if (geoObj is GeometryInstance geoInstance)
                    {
                        GeometryElement instGeometry = geoInstance.GetInstanceGeometry();
                        foreach (GeometryObject instObj in instGeometry)
                        {
                            if (instObj is Line instLine)
                            {
                                gridLineInView = instLine;
                                break;
                            }
                        }
                        if (gridLineInView != null) break;
                    }
                }

                if (gridLineInView == null) return null;

                // Use the line as it appears in the view
                XYZ start3D = gridLineInView.GetEndPoint(0);
                XYZ end3D = gridLineInView.GetEndPoint(1);

                // Convert to our 2D working space
                XYZ start2D = ProjectPointToViewSurface(start3D, view);
                XYZ end2D = ProjectPointToViewSurface(end3D, view);
                XYZ direction2D = (end2D - start2D);

                if (direction2D.GetLength() < 0.001) return null;

                direction2D = direction2D.Normalize();
                XYZ center2D = (start2D + end2D) / 2;

                return new ProjectedItem
                {
                    Element = grid,
                    GeometricReference = new Reference(grid),
                    ProjectedDirection = direction2D,
                    ProjectedPoint = center2D,
                    ItemType = "Grid",
                    IsSelected = false,
                    IsPointElement = false
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error processing grid {grid.Name} in section: {ex.Message}");
                return null;
            }
        }

        private ProjectedItem ProcessGridInLayoutView(RevitGrid grid, View view)
        {
            try
            {
                if (!(grid.Curve is Line gridLine)) return null;

                // Get grid line points
                XYZ start3D = gridLine.GetEndPoint(0);
                XYZ end3D = gridLine.GetEndPoint(1);

                // Project to view surface
                XYZ start2D = ProjectPointToViewSurface(start3D, view);
                XYZ end2D = ProjectPointToViewSurface(end3D, view);

                XYZ direction2D = (end2D - start2D);
                if (direction2D.GetLength() < 0.001) return null;

                direction2D = direction2D.Normalize();
                XYZ center2D = (start2D + end2D) / 2;

                return new ProjectedItem
                {
                    Element = grid,
                    GeometricReference = new Reference(grid),
                    ProjectedDirection = direction2D,
                    ProjectedPoint = center2D,
                    ItemType = "Grid",
                    IsSelected = false,
                    IsPointElement = false
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error processing grid {grid.Name} in layout: {ex.Message}");
                return null;
            }
        }

        private ProjectedItem ProcessLevel(Level level, View view)
        {
            try
            {
                // For section/elevation views only
                if (view.ViewType != ViewType.Section && view.ViewType != ViewType.Elevation)
                    return null;

                // Try to get view-specific geometry first
                Options geometryOptions = new Options();
                geometryOptions.View = view;
                geometryOptions.IncludeNonVisibleObjects = false;

                GeometryElement geometryElement = level.get_Geometry(geometryOptions);

                if (geometryElement != null)
                {
                    // Look for level line in view-specific geometry
                    foreach (GeometryObject geoObj in geometryElement)
                    {
                        if (geoObj is Line levelLine)
                        {
                            XYZ start3D = levelLine.GetEndPoint(0);
                            XYZ end3D = levelLine.GetEndPoint(1);

                            // Convert to 2D working space
                            XYZ start2D = ProjectPointToViewSurface(start3D, view);
                            XYZ end2D = ProjectPointToViewSurface(end3D, view);
                            XYZ levelDirection2D = (end2D - start2D);

                            if (levelDirection2D.GetLength() > 0.001)
                            {
                                levelDirection2D = levelDirection2D.Normalize();
                                XYZ center2D = (start2D + end2D) / 2;

                                return new ProjectedItem
                                {
                                    Element = level,
                                    GeometricReference = new Reference(level),
                                    ProjectedDirection = levelDirection2D,
                                    ProjectedPoint = center2D,
                                    ItemType = "Level",
                                    IsSelected = false,
                                    IsPointElement = false
                                };
                            }
                        }
                    }
                }

                // Fallback: Create level representation based on elevation
                double levelElevation = level.Elevation;
                XYZ levelPoint3D = new XYZ(0, 0, levelElevation);
                XYZ levelPoint2D = ProjectPointToViewSurface(levelPoint3D, view);
                XYZ fallbackDirection2D = new XYZ(1, 0, 0); // Horizontal in view

                return new ProjectedItem
                {
                    Element = level,
                    GeometricReference = new Reference(level),
                    ProjectedDirection = fallbackDirection2D,
                    ProjectedPoint = levelPoint2D,
                    ItemType = "Level",
                    IsSelected = false,
                    IsPointElement = false
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error processing level {level.Name}: {ex.Message}");
                return null;
            }
        }

        private ProjectedItem ProcessElement(Element element, View view)
        {
            try
            {
                if (!(element.Location is LocationCurve locationCurve)) return null;

                Curve curve = locationCurve.Curve;
                XYZ start3D = curve.GetEndPoint(0);
                XYZ end3D = curve.GetEndPoint(1);

                // Project element centerline to view surface
                XYZ start2D = ProjectPointToViewSurface(start3D, view);
                XYZ end2D = ProjectPointToViewSurface(end3D, view);

                XYZ direction2D = (end2D - start2D);
                if (direction2D.GetLength() < 0.001) return null; // This will be caught as perpendicular element

                direction2D = direction2D.Normalize();
                XYZ center2D = (start2D + end2D) / 2;

                Reference centerlineRef = GetCenterlineReference(element);

                return new ProjectedItem
                {
                    Element = element,
                    GeometricReference = centerlineRef,
                    ProjectedDirection = direction2D,
                    ProjectedPoint = center2D,
                    ItemType = "Element",
                    IsSelected = true,
                    IsPointElement = false
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error processing element: {ex.Message}");
                return null;
            }
        }

        // Project 3D point to correct view surface
        private XYZ ProjectPointToViewSurface(XYZ point3D, View view)
        {
            switch (view.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.AreaPlan:
                    return new XYZ(point3D.X, point3D.Y, 0);

                case ViewType.Section:
                    XYZ sectionRight = view.RightDirection.Normalize();
                    XYZ sectionUp = view.UpDirection.Normalize();
                    XYZ viewOrigin = view.Origin;
                    XYZ relativePoint = point3D - viewOrigin;

                    double x = relativePoint.DotProduct(sectionRight);
                    double y = relativePoint.DotProduct(sectionUp);
                    return new XYZ(x, y, 0);

                case ViewType.Elevation:
                    XYZ elevRight = view.RightDirection.Normalize();
                    XYZ elevUp = view.UpDirection.Normalize();
                    XYZ elevOrigin = view.Origin;
                    XYZ elevRelative = point3D - elevOrigin;

                    double elevX = elevRelative.DotProduct(elevRight);
                    double elevY = elevRelative.DotProduct(elevUp);
                    return new XYZ(elevX, elevY, 0);

                default:
                    return new XYZ(point3D.X, point3D.Y, 0);
            }
        }

        private bool HasCenterline(Element element)
        {
            if (element == null || element.Category == null) return false;
            if (element is RevitGrid || element is Level) return false;

            // Exclude fittings, accessories
            if (element is FamilyInstance)
            {
                string categoryName = element.Category.Name?.ToLower() ?? "";
                if (categoryName.Contains("fitting") ||
                    categoryName.Contains("accessory") ||
                    categoryName.Contains("insulation"))
                {
                    return false;
                }
            }

            return element.Location is LocationCurve;
        }

        private Reference GetCenterlineReference(Element element)
        {
            try
            {
                if (element is FamilyInstance familyInstance)
                {
                    var centerlineRefs = familyInstance.GetReferences(FamilyInstanceReferenceType.CenterLeftRight);
                    if (centerlineRefs != null && centerlineRefs.Count > 0)
                        return centerlineRefs.First();

                    var elevationRefs = familyInstance.GetReferences(FamilyInstanceReferenceType.CenterElevation);
                    if (elevationRefs != null && elevationRefs.Count > 0)
                        return elevationRefs.First();
                }

                return new Reference(element);
            }
            catch
            {
                return new Reference(element);
            }
        }

        // STEP 2: Group by parallel directions - UPDATED to handle point elements
        private Dictionary<XYZ, List<ProjectedItem>> GroupByParallelDirections(List<ProjectedItem> projectedItems)
        {
            var groups = new Dictionary<XYZ, List<ProjectedItem>>();

            System.Diagnostics.Debug.WriteLine($"=== GROUPING {projectedItems.Count} ITEMS BY PARALLEL DIRECTIONS ===");

            // Separate point elements and linear elements
            var pointElements = projectedItems.Where(item => item.IsPointElement).ToList();
            var linearElements = projectedItems.Where(item => !item.IsPointElement).ToList();

            System.Diagnostics.Debug.WriteLine($"Point elements: {pointElements.Count}, Linear elements: {linearElements.Count}");

            // Process linear elements first to establish direction groups
            foreach (var item in linearElements)
            {
                XYZ direction = item.ProjectedDirection;
                XYZ groupDirection = null;

                // Find existing parallel group
                foreach (var existingDirection in groups.Keys)
                {
                    if (AreDirectionsParallel(direction, existingDirection))
                    {
                        groupDirection = existingDirection;
                        break;
                    }
                }

                // Create new group if needed
                if (groupDirection == null)
                {
                    groupDirection = direction;
                    groups[groupDirection] = new List<ProjectedItem>();
                }

                groups[groupDirection].Add(item);
            }

            // Add point elements to ALL direction groups
            foreach (var pointElement in pointElements)
            {
                foreach (var group in groups.Values)
                {
                    group.Add(pointElement);
                }

                // If no groups exist yet, create a default horizontal group
                if (groups.Count == 0)
                {
                    XYZ defaultDirection = new XYZ(1, 0, 0);
                    groups[defaultDirection] = new List<ProjectedItem> { pointElement };
                }
            }

            return groups;
        }

        private bool AreDirectionsParallel(XYZ dir1, XYZ dir2)
        {
            double cross = Math.Abs(dir1.X * dir2.Y - dir1.Y * dir2.X);
            return cross < PARALLEL_TOLERANCE;
        }

        // STEP 3: Create dimension chains with CORRECT collinearity logic
        private Dictionary<XYZ, List<ProjectedItem>> CreateCombinedDimensionGroups(Dictionary<XYZ, List<ProjectedItem>> parallelGroups)
        {
            var dimensionGroups = new Dictionary<XYZ, List<ProjectedItem>>();

            foreach (var parallelGroup in parallelGroups)
            {
                XYZ direction = parallelGroup.Key;
                List<ProjectedItem> allParallelItems = parallelGroup.Value;

                // Calculate position along direction for sorting
                foreach (var item in allParallelItems)
                {
                    item.PositionAlongDirection = item.ProjectedPoint.DotProduct(direction);
                }

                // Check if we have any selected elements in this parallel group
                var selectedElements = allParallelItems.Where(i => (i.ItemType == "Element" || i.ItemType == "PerpendicularElement") && i.IsSelected).ToList();

                if (selectedElements.Count == 0) continue;

                // Find collinear groups
                List<List<ProjectedItem>> trueCollinearGroups = FindTrueCollinearGroups(allParallelItems, direction);

                // Select one representative from each collinear group
                List<ProjectedItem> representatives = new List<ProjectedItem>();
                foreach (var group in trueCollinearGroups)
                {
                    ProjectedItem rep = SelectBestRepresentative(group);
                    representatives.Add(rep);
                }

                // Create final dimension chain
                if (representatives.Count >= 2)
                {
                    representatives.Sort((a, b) => a.PositionAlongDirection.CompareTo(b.PositionAlongDirection));
                    dimensionGroups[direction] = representatives;
                }
            }

            return dimensionGroups;
        }

        private List<List<ProjectedItem>> FindTrueCollinearGroups(List<ProjectedItem> parallelItems, XYZ groupDirection)
        {
            var trueCollinearGroups = new List<List<ProjectedItem>>();
            const double LINE_TOLERANCE = 0.01; // 0.01 feet tolerance

            foreach (var item in parallelItems)
            {
                bool addedToExistingGroup = false;

                foreach (var group in trueCollinearGroups)
                {
                    if (LiesOnSameInfiniteLine(item, group[0], groupDirection, LINE_TOLERANCE))
                    {
                        group.Add(item);
                        addedToExistingGroup = true;
                        break;
                    }
                }

                if (!addedToExistingGroup)
                {
                    trueCollinearGroups.Add(new List<ProjectedItem> { item });
                }
            }

            return trueCollinearGroups;
        }

        private bool LiesOnSameInfiniteLine(ProjectedItem item1, ProjectedItem item2, XYZ groupDirection, double tolerance)
        {
            XYZ perpDirection = new XYZ(-groupDirection.Y, groupDirection.X, 0);
            if (perpDirection.GetLength() < 0.001)
            {
                perpDirection = new XYZ(0, 1, 0);
            }
            perpDirection = perpDirection.Normalize();

            XYZ pointDifference = item2.ProjectedPoint - item1.ProjectedPoint;
            double perpendicularDistance = Math.Abs(pointDifference.DotProduct(perpDirection));

            return perpendicularDistance <= tolerance;
        }

        private ProjectedItem SelectBestRepresentative(List<ProjectedItem> group)
        {
            if (group.Count == 1) return group[0];

            // Priority: Selected Elements > Grids > Levels > Other Elements
            var selectedElements = group.Where(i => (i.ItemType == "Element" || i.ItemType == "PerpendicularElement") && i.IsSelected).ToList();
            if (selectedElements.Count > 0)
            {
                var linearSelected = selectedElements.Where(i => !i.IsPointElement).ToList();
                if (linearSelected.Count > 0)
                {
                    return linearSelected.First();
                }
                return selectedElements.First();
            }

            var grids = group.Where(i => i.ItemType == "Grid").ToList();
            if (grids.Count > 0)
            {
                var namedGrid = grids.FirstOrDefault(g => !string.IsNullOrEmpty(g.Element.Name));
                return namedGrid ?? grids.First();
            }

            var levels = group.Where(i => i.ItemType == "Level").ToList();
            if (levels.Count > 0)
            {
                var namedLevel = levels.FirstOrDefault(l => !string.IsNullOrEmpty(l.Element.Name));
                return namedLevel ?? levels.First();
            }

            var linearElements = group.Where(i => !i.IsPointElement).ToList();
            if (linearElements.Count > 0)
            {
                return linearElements.First();
            }

            return group.First();
        }

        // STEP 4: Create dimension chain
        private Dimension CreateDimensionChain(Document doc, View view, XYZ direction, List<ProjectedItem> items)
        {
            try
            {
                ReferenceArray refArray = new ReferenceArray();
                foreach (var item in items)
                {
                    refArray.Append(item.GeometricReference);
                }

                XYZ perpDirection = new XYZ(-direction.Y, direction.X, 0);
                if (perpDirection.GetLength() < 0.001)
                {
                    perpDirection = new XYZ(0, 1, 0);
                }
                perpDirection = perpDirection.Normalize();

                XYZ groupCenter = XYZ.Zero;
                foreach (var item in items)
                {
                    groupCenter += item.ProjectedPoint;
                }
                groupCenter = groupCenter / items.Count;

                XYZ dimLinePoint2D = groupCenter + perpDirection * DIMENSION_OFFSET;
                XYZ dimLinePoint3D = ConvertViewPointTo3D(dimLinePoint2D, view);
                XYZ direction3D = ConvertViewDirectionTo3D(direction, view);

                if (direction3D.GetLength() < 0.001)
                {
                    direction3D = new XYZ(1, 0, 0);
                }
                else
                {
                    direction3D = direction3D.Normalize();
                }

                double minProj = items.Min(item => item.PositionAlongDirection);
                double maxProj = items.Max(item => item.PositionAlongDirection);

                double span = Math.Abs(maxProj - minProj);
                if (span < 1.0)
                {
                    double center = (minProj + maxProj) / 2;
                    minProj = center - 0.5;
                    maxProj = center + 0.5;
                }

                XYZ dimStart3D = dimLinePoint3D + direction3D * (minProj - 1.97);
                XYZ dimEnd3D = dimLinePoint3D + direction3D * (maxProj + 1.97);

                Line dimensionLine = Line.CreateBound(dimStart3D, dimEnd3D);
                Dimension dimension = doc.Create.NewDimension(view, dimensionLine, refArray);

                return dimension;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating dimension: {ex.Message}");
                return null;
            }
        }

        private XYZ ConvertViewPointTo3D(XYZ point2D, View view)
        {
            switch (view.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.AreaPlan:
                    return new XYZ(point2D.X, point2D.Y, 0);

                case ViewType.Section:
                case ViewType.Elevation:
                    XYZ viewOrigin = view.Origin;
                    XYZ viewRight = view.RightDirection.Normalize();
                    XYZ viewUp = view.UpDirection.Normalize();
                    return viewOrigin + viewRight * point2D.X + viewUp * point2D.Y;

                default:
                    return new XYZ(point2D.X, point2D.Y, 0);
            }
        }

        private XYZ ConvertViewDirectionTo3D(XYZ direction2D, View view)
        {
            switch (view.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.AreaPlan:
                    return new XYZ(direction2D.X, direction2D.Y, 0).Normalize();

                case ViewType.Section:
                case ViewType.Elevation:
                    XYZ viewRight = view.RightDirection.Normalize();
                    XYZ viewUp = view.UpDirection.Normalize();
                    return (viewRight * direction2D.X + viewUp * direction2D.Y).Normalize();

                default:
                    return new XYZ(direction2D.X, direction2D.Y, 0).Normalize();
            }
        }

        // STEP 5: Move dimensions left
        private void MoveDimensionsLeft(Document doc, List<Dimension> dimensions, double moveDistance)
        {
            foreach (Dimension dimension in dimensions)
            {
                try
                {
                    Line dimensionLine = dimension.Curve as Line;
                    if (dimensionLine == null) continue;

                    XYZ dimensionDirection = dimensionLine.Direction.Normalize();
                    XYZ leftDirection = new XYZ(-dimensionDirection.Y, dimensionDirection.X, 0);

                    if (leftDirection.GetLength() < 0.1)
                        leftDirection = new XYZ(-1, 0, 0);

                    leftDirection = leftDirection.Normalize();
                    XYZ translationVector = leftDirection * moveDistance;

                    ElementTransformUtils.MoveElement(doc, dimension.Id, translationVector);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error moving dimension: {ex.Message}");
                }
            }
        }
    }
}