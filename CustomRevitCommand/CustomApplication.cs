using System;
using System.IO;
using System.Windows.Media.Imaging;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace CustomRevitCommand
{
    // External Application class - handles add-in initialization
    public class CustomApplication : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication a)
        {
            string tabName = "DEAXO Draw";
            try
            {
                a.CreateRibbonTab(tabName);
            }
            catch
            {
                // Tab might already exist
            }

            RibbonPanel ribbonPanel = a.CreateRibbonPanel(tabName, "Smart Dimensions");
            string thisAssemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;

            // First button: Auto-Dimension
            PushButtonData buttonData = new PushButtonData(
                "AutoDimensionCommand",
                "Auto-\nDimension",
                thisAssemblyPath,
                "CustomRevitCommand.AutoDimensionCommand"
            );
            buttonData.ToolTip = "Creates dimension chains including ALL grids and levels between elements";

            // Add icon to the button
            try
            {
                string iconPath = Path.Combine(Path.GetDirectoryName(thisAssemblyPath), "Icons");
                if (Directory.Exists(iconPath))
                {
                    string largeIcon = Path.Combine(iconPath, "AutoDimension32.png");
                    string smallIcon = Path.Combine(iconPath, "AutoDimension16.png");

                    if (File.Exists(largeIcon))
                        buttonData.LargeImage = new BitmapImage(new Uri(largeIcon));

                    if (File.Exists(smallIcon))
                        buttonData.Image = new BitmapImage(new Uri(smallIcon));
                }
            }
            catch (Exception ex)
            {
                // If icon loading fails, continue without icon
                System.Diagnostics.Debug.WriteLine($"Could not load icon: {ex.Message}");
            }

            PushButton pushButton = ribbonPanel.AddItem(buttonData) as PushButton;

            // Second button: Dimension Chain
            PushButtonData chainButtonData = new PushButtonData(
                "DimensionChainCommand",
                "Dimension\nChain",
                thisAssemblyPath,
                "CustomRevitCommand.DimensionChainCommand"
            );
            chainButtonData.ToolTip = "Create dimension chain by defining direction line and placement point";

            // Add icon for chain command
            try
            {
                string iconPath = Path.Combine(Path.GetDirectoryName(thisAssemblyPath), "Icons");
                if (Directory.Exists(iconPath))
                {
                    string largeIcon = Path.Combine(iconPath, "DimensionChain32.png");
                    string smallIcon = Path.Combine(iconPath, "DimensionChain16.png");

                    if (File.Exists(largeIcon))
                        chainButtonData.LargeImage = new BitmapImage(new Uri(largeIcon));

                    if (File.Exists(smallIcon))
                        chainButtonData.Image = new BitmapImage(new Uri(smallIcon));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not load chain icon: {ex.Message}");
            }

            PushButton chainButton = ribbonPanel.AddItem(chainButtonData) as PushButton;

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication a)
        {
            return Result.Succeeded;
        }
    }
}