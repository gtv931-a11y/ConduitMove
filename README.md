# AlignElements

A **Revit 2026** add-in built for BIM modeling and coordination workflows, focused on streamlining element alignment, connector matching, and connection tasks.

## Key Features
- Align conduits and connectors efficiently during modeling or coordination.
- Cycle through connectors and stretch or switch connectors directly from the same palette.
- Includes a standalone installer for straightforward deployment.

## Compatibility
- **Autodesk Revit:** 2026

## Installation
1. Go to the **Releases** section of this repository.
2. Download `AlignElements_Revit2026_Setup.exe` from the latest release (`v1.0.0`).
3. Run the installer and follow the setup prompts.
4. Launch **Revit 2026** and verify the add-in is available.

## Repository Structure
- `AlignElements.csproj` & `AlignElements.addin` — Project configuration and Revit add-in manifest.
- `alignelementsapp.cs` & `connectorpalette.cs` — Application entry point and UI/palette components.
- `AlignPipes/`, `AlignPipesWithConnectors/`, `AlignConnectorToConduits/`, `DiameterConnect/`, `StretchPipesToConnectors/` — Core alignment and connection commands.
