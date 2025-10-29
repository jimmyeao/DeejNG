namespace DeejNG.Models
{
    public class ThemeOption
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Icon { get; set; }
        public string ThemeFile { get; set; }

        public ThemeOption(string name, string displayName, string icon, string themeFile)
        {
            Name = name;
            DisplayName = displayName;
            Icon = icon;
            ThemeFile = themeFile;
        }

        public override string ToString() => DisplayName;
    }
}
