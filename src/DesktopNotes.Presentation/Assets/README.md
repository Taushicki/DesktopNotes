# DesktopNotes icons

The selected “Links” concept uses a yellow note and a blue note joined by an elbow connector.
`AppIcon-source.png` is the production artwork generated with the built-in ImageGen tool.
`App.ico` supplies the executable, installer and settings window icon.
`TrayLight.ico` and `TrayDark.ico` are simplified, transparent glyphs for dark and light taskbars respectively.

Rebuild the committed ICO assets with `./scripts/build-icons.ps1` from the repository root.
The script packages multiple resolutions, applies the rounded application icon mask and draws the tray glyphs directly at each target size.

## Artwork prompts

1. Using the selected third concept as reference, extract only the large application icon: a pale rounded square tile, a yellow folded note at upper left, a blue folded note at lower right and a dark elbow connector. Preserve its colors, proportions and soft dimensional appearance. Remove labels, comparison panels and tray previews. Center on a transparent background with a small safe margin.
2. Preserve the icon interior and remove all stray pixels outside the rounded tile. Use a smooth clean silhouette and transparent surroundings, with approximately 5% margin. No labels, extra objects or backdrop.

The source artwork is retained for future revisions; it is not embedded in the application.
