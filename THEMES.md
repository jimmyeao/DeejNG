# DeejNG Theme System

## Available Themes

DeejNG now supports 9 beautiful themes matching the Mideej app:

1. **🌑 Dark** - The default dark theme
2. **☀️ Light** - Clean light theme
3. **❄️ Arctic** - Cool blue tones
4. **🔮 Cyberpunk** - Neon purple/pink vibes
5. **🧛 Dracula** - Popular developer theme
6. **🌲 Forest** - Nature-inspired greens
7. **🌌 Nord** - Polar aurora colors
8. **🌊 Ocean** - Deep sea blues
9. **🌅 Sunset** - Warm orange/pink gradient

## How to Use

- Select a theme from the dropdown in the toolbar (top right, next to settings gear)
- Theme selection is automatically saved to your active profile
- Themes apply instantly across the entire application

## Theme Structure

Each theme is defined in `/Themes/*Theme.xaml` and provides:
- Background colors
- Surface/card colors
- Primary/accent colors
- Text colors (primary & secondary)
- Border colors
- Status colors (error, warning, success)

All themes use the same brush keys for consistency:
- `BackgroundBrush` - Main window background
- `SurfaceBrush` - Cards, toolbar, title bar
- `SurfaceHighlightBrush` - Hover states
- `PrimaryBrush` - Buttons, accents
- `TextPrimaryBrush` - Main text
- `TextSecondaryBrush` - Secondary text
- `BorderBrush` - Borders and dividers
- `ErrorBrush`, `WarningBrush`, `SuccessBrush` - Status indicators

## Adding Custom Themes

1. Create a new XAML file in `/Themes/` (e.g., `MyTheme.xaml`)
2. Copy the structure from any existing theme
3. Modify the color values
4. Add the theme to `MainWindow.InitializeThemeSelector()`:
   ```csharp
   new ThemeOption("MyTheme", "My Theme", "🎨", "/Themes/MyTheme.xaml")
   ```
5. Rebuild and your theme will appear in the selector
