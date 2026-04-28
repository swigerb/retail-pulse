# Tenant Configuration Guide

Retail Pulse is fully tenant-configurable. Define your company, brands, regions, channels, and theme in a single `tenant.yaml` file and the entire platform adapts — prompts, data, UI, and Teams bot all reflect your tenant.

---

## Quick Start

1. Edit `tenant.yaml` in the repo root
2. Restart the application — all services pick up the new configuration automatically

---

## Schema Reference

```yaml
# Required — your company name
company: "Apex Brands"

# Required — industry vertical
industry: "Spirits & Beverages"

# Optional — description used in AI agent prompts
description: "Apex Brands is a premium spirits company with a diverse portfolio..."

# Required — list of brands
brands:
  - name: "Sierra Gold Tequila"       # Display name
    category: "Tequila"               # Product category
    variants:                          # Product variants/SKUs
      - "Blanco"
      - "Reposado"
      - "Añejo"
    priceSegment: "Premium"            # Pricing tier

# Required — geographic regions
regions:
  - "Northeast"
  - "Southeast"
  - "Midwest"

# Optional — sales channels
channels:
  - "On-Premise"
  - "Off-Premise"
  - "E-Commerce"

# Optional — UI theme customization
theme:
  primaryColor: "#1B4D7A"              # Header, primary buttons
  accentColor: "#E8A838"               # Highlights, accents
  logoPath: "assets/apex-logo.png"     # Logo file path
  fontFamily: "Inter, system-ui, sans-serif"

# Optional — distribution model
distribution:
  model: "Three-Tier"
  distributorTypes:
    - "Distributor"
    - "Wholesaler"
    - "Retailer"
```

---

## Field Reference

### Company & Industry

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `company` | string | Yes | Company name displayed in UI and prompts |
| `industry` | string | Yes | Industry vertical (e.g., "Spirits & Beverages") |
| `description` | string | No | Company description injected into AI agent system prompt |

### Brands

Each brand object supports:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | Yes | Brand display name |
| `category` | string | Yes | Product category (e.g., "Tequila", "Vodka") |
| `variants` | string[] | No | Product variants or SKUs |
| `priceSegment` | string | No | Price tier: "Standard", "Premium", "Ultra-Premium" |

### Regions

A flat list of geographic region names. These are used for:
- Data filtering in MCP tools
- AI agent prompt context
- Dashboard region selectors

### Channels

Sales channel names (e.g., "On-Premise", "Off-Premise", "E-Commerce"). Used in data filtering and analytics.

### Theme

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `primaryColor` | hex string | No | Primary brand color for headers and buttons |
| `accentColor` | hex string | No | Accent color for highlights |
| `logoPath` | string | No | Path to logo image |
| `fontFamily` | string | No | CSS font-family value |

The React frontend reads theme values and applies them via CSS custom properties, so the UI automatically matches your branding.

### Distribution

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `model` | string | No | Distribution model name (e.g., "Three-Tier") |
| `distributorTypes` | string[] | No | Types of distribution entities |

---

## Industry Examples

### Spirits & Beverages (included sample)

```yaml
company: "Apex Brands"
industry: "Spirits & Beverages"
brands:
  - name: "Sierra Gold Tequila"
    category: "Tequila"
    variants: ["Blanco", "Reposado", "Añejo", "Extra Añejo"]
    priceSegment: "Premium"
  - name: "Summit Vodka"
    category: "Vodka"
    variants: ["Original", "Citrus", "Pepper"]
    priceSegment: "Premium"
  - name: "Ridgeline Bourbon"
    category: "Bourbon"
    variants: ["Small Batch", "Single Barrel"]
    priceSegment: "Premium"
regions:
  - "Northeast"
  - "Southeast"
  - "Midwest"
  - "Southwest"
  - "West Coast"
channels:
  - "On-Premise"
  - "Off-Premise"
  - "E-Commerce"
distribution:
  model: "Three-Tier"
  distributorTypes: ["Distributor", "Wholesaler", "Retailer"]
theme:
  primaryColor: "#1B4D7A"
  accentColor: "#E8A838"
```

### Snack Foods

```yaml
company: "Crunch Co."
industry: "Snack Foods"
brands:
  - name: "Crunch Chips"
    category: "Potato Chips"
    variants: ["Classic", "BBQ", "Sour Cream", "Salt & Vinegar"]
    priceSegment: "Standard"
  - name: "Nutty Delights"
    category: "Mixed Nuts"
    variants: ["Roasted", "Honey Glazed", "Spicy"]
    priceSegment: "Premium"
  - name: "Crunch Pretzels"
    category: "Pretzels"
    variants: ["Traditional", "Honey Mustard", "Cheddar"]
    priceSegment: "Standard"
regions:
  - "East Coast"
  - "Central"
  - "West Coast"
channels:
  - "Grocery"
  - "Convenience"
  - "Club Stores"
  - "E-Commerce"
distribution:
  model: "Direct Store Delivery"
  distributorTypes: ["Distributor", "Retailer"]
theme:
  primaryColor: "#D4380D"
  accentColor: "#FFA940"
```

### Craft Beverages

```yaml
company: "Cascade Brewing Collective"
industry: "Craft Beverages"
brands:
  - name: "Cascade IPA"
    category: "Beer"
    variants: ["West Coast IPA", "Hazy IPA", "Session IPA"]
    priceSegment: "Premium"
  - name: "Mountain Cider"
    category: "Hard Cider"
    variants: ["Dry", "Semi-Sweet", "Hopped"]
    priceSegment: "Premium"
  - name: "Cascade Seltzer"
    category: "Hard Seltzer"
    variants: ["Lime", "Grapefruit", "Mango", "Mixed Berry"]
    priceSegment: "Standard"
regions:
  - "Pacific Northwest"
  - "Northern California"
  - "Mountain West"
channels:
  - "Taproom"
  - "On-Premise"
  - "Off-Premise"
distribution:
  model: "Three-Tier"
  distributorTypes: ["Distributor", "Retailer"]
theme:
  primaryColor: "#135200"
  accentColor: "#73D13D"
```

---

## How Tenant Configuration Flows Through the System

1. **Prompts** — `prompts.yaml` uses `{{tenant.*}}` template placeholders. The AI agent's system prompt is automatically populated with your company name, brands, and regions.

2. **Simulated Data** — `SimulatedMetricsData` generates realistic data based on the brands and regions defined in `tenant.yaml`.

3. **React UI** — CSS custom properties are set from `theme` values. The dashboard header, sidebar, and charts all reflect your brand colors.

4. **Teams Bot** — Adaptive Card templates use tenant configuration for branding and suggested queries.

---

## Theme Customization

The React frontend applies theme colors via CSS custom properties:

```css
:root {
  --color-primary: var(--tenant-primary, #1B4D7A);
  --color-accent: var(--tenant-accent, #E8A838);
  --font-family: var(--tenant-font, 'Inter, system-ui, sans-serif');
}
```

To see your theme changes:
1. Update the `theme` section in `tenant.yaml`
2. Restart the application
3. The dashboard immediately reflects the new colors

### Color Guidelines

- **Primary color** — Used for headers, navigation, and primary buttons. Choose a dark, readable color.
- **Accent color** — Used for highlights, active states, and charts. Choose a contrasting, vibrant color.
- Ensure sufficient contrast between primary/accent colors and text for accessibility.
