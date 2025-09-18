using Autodesk.Revit.DB;

namespace CustomRevitCommand
{
    public class ProjectedItem
    {
        public Element Element { get; set; }
        public Reference GeometricReference { get; set; }
        public XYZ ProjectedDirection { get; set; }
        public XYZ ProjectedPoint { get; set; }
        public double PositionAlongDirection { get; set; }
        public string ItemType { get; set; }
        public bool IsSelected { get; set; }
        public bool IsPointElement { get; set; }
    }
}