// =====================================================================================
//  DuctAlignApp.cs
//
//  Independent ribbon registration for the RIGID DUCT module. Creates its own panel ("AlignDucts")
//  so alignelementsapp.cs (pipes / conduits) stays untouched.
//
//  Needs its own .addin <AddIn Type="Application"> entry pointing to DuctAlignSuite.DuctAlignApp,
//  or, if you prefer a single entry, call the body of OnStartup from your existing application class.
//
//  Icons (optional, next to the DLL): DuctAlign.png, DuctAlignDucts.png, DuctAlignToConnector.png,
//  DuctStretchToConnector.png, DuctConnectorToDuct.png. Missing files fall back to the existing icons.
// =====================================================================================

using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace DuctAlignSuite
{
    public class DuctAlignApp : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                string asmPath = Assembly.GetExecutingAssembly().Location;
                string dir = Path.GetDirectoryName(asmPath) ?? "";

                RibbonPanel panel = application.CreateRibbonPanel(Tab.AddIns, "AlignDucts");

                var pdData = new PulldownButtonData(
                    "AlignDuctsMenu",
                    "Ducts Smart" + Environment.NewLine + "Align Duct");
                pdData.ToolTip = "3D alignment tools for RIGID ducts (flexible ducts are excluded).";
                SetIcon(pdData, dir, "DuctAlign.png", "AlignElements.png");

                var pd = panel.AddItem(pdData) as PulldownButton;

                var btnDucts = new PushButtonData(
                    "AlignDuctsToDuctBtn",
                    "Align" + Environment.NewLine + "Ducts",
                    asmPath,
                    "DuctAligner.DuctAlignDuctsCommand");
                btnDucts.ToolTip = "Aligns rigid ducts relative to a reference rigid duct.";
                SetIcon(btnDucts, dir, "DuctAlignDucts.png", "PipeAligner.png");
                pd.AddPushButton(btnDucts);

                var btnConn = new PushButtonData(
                    "AlignDuctToConnectorBtn",
                    "Align Duct To" + Environment.NewLine + "Connector",
                    asmPath,
                    "DuctAligner.DuctAlignToConnectorCommand");
                btnConn.ToolTip = "Aligns a rigid duct to an HVAC connector of a family (profile shape must match).";
                SetIcon(btnConn, dir, "DuctAlignToConnector.png", "AlignToConnector.png");
                pd.AddPushButton(btnConn);

                var btnStretch = new PushButtonData(
                    "StretchDuctToConnectorBtn",
                    "Stretch Duct to" + Environment.NewLine + "Connector",
                    asmPath,
                    "DuctAligner.DuctStretchToConnectorCommand");
                btnStretch.ToolTip = "Stretches the nearest end of a rigid duct to an HVAC connector and connects it if coaxial.";
                SetIcon(btnStretch, dir, "DuctStretchToConnector.png", "StretchToConnector.png");
                pd.AddPushButton(btnStretch);

                var btnC2D = new PushButtonData(
                    "AlignConnectorToDuctBtn",
                    "Align Connector" + Environment.NewLine + "Element to Duct",
                    asmPath,
                    "DuctAligner.DuctConnectorToDuctCommand");
                btnC2D.ToolTip = "Moves a family so one of its HVAC connectors lands on the axis of a rigid duct.";
                SetIcon(btnC2D, dir, "DuctConnectorToDuct.png", "AlignConnetorToConduit.png");
                pd.AddPushButton(btnC2D);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("AlignDucts", "Could not create the menu: " + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        private static void SetIcon(ButtonData data, string dir, string primary, string fallback)
        {
            foreach (string name in new[] { primary, fallback })
            {
                string path = Path.Combine(dir, name);
                if (!File.Exists(path)) continue;
                try
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.UriSource = new Uri(path, UriKind.Absolute);
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.EndInit();
                    img.Freeze();
                    data.LargeImage = img;
                    data.Image = img;
                    return;
                }
                catch { }
            }
        }
    }
}
