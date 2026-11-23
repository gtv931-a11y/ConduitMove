using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace AlignElementsSuite
{
    public class AlignElementsApp : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                string asmPath = Assembly.GetExecutingAssembly().Location;
                string dir = Path.GetDirectoryName(asmPath) ?? "";

                RibbonPanel panel = application.CreateRibbonPanel(Tab.AddIns, "AlignElements");

                var pdData = new PulldownButtonData("AlignElementsMenu", "Align" + Environment.NewLine + "Elements");
                pdData.ToolTip = "3D MEP alignment tools.";
                SetIcon(pdData, Path.Combine(dir, "AlignElements.png"));

                var pd = panel.AddItem(pdData) as PulldownButton;

                var btnPipes = new PushButtonData(
                    "AlignPipesBtn",
                    "Align" + Environment.NewLine + "Conduits/Pipes",
                    asmPath,
                    "PipeAligner.AlinearTuberiasCommand");
                btnPipes.ToolTip = "Aligns conduits and pipes relative to a reference segment.";
                SetIcon(btnPipes, Path.Combine(dir, "PipeAligner.png"));
                pd.AddPushButton(btnPipes);

                var btnConn = new PushButtonData(
                    "AlignToConnectorBtn",
                    "Align To" + Environment.NewLine + "Connector",
                    asmPath,
                    "AlignToConnector.AlinearConectorCommand");
                btnConn.ToolTip = "Aligns a pipe or conduit to a family connector.";
                SetIcon(btnConn, Path.Combine(dir, "AlignToConnector.png"));
                pd.AddPushButton(btnConn);

                var btnStretch = new PushButtonData(
                    "StretchToConnectorBtn",
                    "Stretch to Connector" + Environment.NewLine + "(Horizontal)",
                    asmPath,
                    "StretchConnectors.EstirarConectorCommand");
                btnStretch.ToolTip = "Stretches the nearest end of a pipe or conduit horizontally to a family connector.";
                SetIcon(btnStretch, Path.Combine(dir, "StretchToConnector.png"));
                pd.AddPushButton(btnStretch);

                var btnDiam = new PushButtonData(
                    "DiameterConnectorPipeBtn",
                    "Stretch Vertical" + Environment.NewLine + "& Connect",
                    asmPath,
                    "PipeAlignerVertical.ConectarVerticalCommand");
                btnDiam.ToolTip = "Stretches a segment vertically to a family connector, matches diameter, and connects it.";
                SetIcon(btnDiam, Path.Combine(dir, "DiameterConnectorPipe.png"));
                pd.AddPushButton(btnDiam);

                var btnC2P = new PushButtonData(
                    "AlignConnectorToPipeBtn",
                    "Align Connector" + Environment.NewLine + "To Conduit/Pipe",
                    asmPath,
                    "ConnectorToPipe.AlinearConectorATuberiaCommand");
                btnC2P.ToolTip = "Moves a family to align one of its connectors with a conduit or pipe.";
                string c2pIcon = Path.Combine(dir, "AlignConnetorToConduit.png");
                if (!File.Exists(c2pIcon)) c2pIcon = Path.Combine(dir, "AlignToConnector.png");
                SetIcon(btnC2P, c2pIcon);
                pd.AddPushButton(btnC2P);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("AlignElements", "Could not create the menu: " + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        private static void SetIcon(ButtonData data, string pngPath)
        {
            if (!File.Exists(pngPath)) return;
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(pngPath, UriKind.Absolute);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            data.LargeImage = img;
            data.Image = img;
        }
    }
}