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

                var pdData = new PulldownButtonData(
                    "AlignElementsMenu",
                    "Conduits Smart");
                pdData.ToolTip = "3D MEP alignment tools.";
                SetIcon(pdData, Path.Combine(dir, "AlignElements.png"));

                var pd = panel.AddItem(pdData) as PulldownButton;

                var btnPipes = new PushButtonData(
                    "AlignPipesBtn",
                    "Align" + Environment.NewLine + "Conduits",
                    asmPath,
                    "PipeAligner.AlinearTuberiasCommand");
                btnPipes.ToolTip = "Aligns conduits and pipes relative to a reference segment.";
                SetIcon(btnPipes, Path.Combine(dir, "PipeAligner.png"));
                pd.AddPushButton(btnPipes);

                var btnAxes = new PushButtonData(
                    "AlignAxesConduitsBtn",
                    "Align Conduits" + Environment.NewLine + "(Axes)",
                    asmPath,
                    "GestorVistas.AlignConduits.AlignAxesConduitsCommand");
                btnAxes.ToolTip = "Aligns the axes of two conduits and optionally connects them.";
                SetIcon(btnAxes, Path.Combine(dir, "AlignAxesconduit.png"));
                pd.AddPushButton(btnAxes);

                var btnConn = new PushButtonData(
                    "AlignToConnectorBtn",
                    "Conduit to" + Environment.NewLine + "Connector",
                    asmPath,
                    "AlignToConnector.AlinearConectorCommand");
                btnConn.ToolTip = "Aligns a pipe or conduit to a family connector.";
                SetIcon(btnConn, Path.Combine(dir, "AlignToConnector.png"));
                pd.AddPushButton(btnConn);

                // Stretch no aparece en el ribbon: se usa solo desde la paleta (Enter / Space).

                var btnC2P = new PushButtonData(
                    "AlignConnectorToPipeBtn",
                    "Connector to" + Environment.NewLine + "Conduit",
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