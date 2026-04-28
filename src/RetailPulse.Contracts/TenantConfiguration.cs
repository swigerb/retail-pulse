namespace RetailPulse.Contracts;

public class TenantConfiguration
{
    public string Company { get; set; } = "Retail Pulse Demo";
    public string Industry { get; set; } = "Retail & Consumer Goods";
    public string Description { get; set; } = "";
    public List<BrandConfig> Brands { get; set; } = new();
    public List<string> Regions { get; set; } = new();
    public List<string> Channels { get; set; } = new();
    public ThemeConfig Theme { get; set; } = new();
    public DistributionConfig Distribution { get; set; } = new();
}

public class BrandConfig
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public List<string> Variants { get; set; } = new();
    public string PriceSegment { get; set; } = "Premium";
}

public class ThemeConfig
{
    public string PrimaryColor { get; set; } = "#1A73E8";
    public string AccentColor { get; set; } = "#FFC107";
    public string LogoPath { get; set; } = "assets/logo.png";
    public string FontFamily { get; set; } = "Inter, system-ui, sans-serif";
}

public class DistributionConfig
{
    public string Model { get; set; } = "Three-Tier";
    public List<string> DistributorTypes { get; set; } = new() { "Distributor", "Wholesaler", "Retailer" };
}
