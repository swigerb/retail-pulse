using System.Globalization;
using System.Text;

namespace RetailPulse.McpServer.Data;

public static class BacardiSimulatedData
{
    private static readonly Dictionary<(string Brand, string Region), DepletionRecord> DepletionData = new(StringTupleComparer.Instance)
    {
        // ── Patrón Silver ──────────────────────────────────────────
        [("Patrón Silver", "Florida")] = new("+2.1%", "-4.0%", 8.5, "Overstocked",
            "Distributors report consumer shift toward Tequila Ocho at the $45 price point. On-premise velocity in Miami remains high, but suburban retail is lagging. Costco depletions down 11% vs prior year."),
        [("Patrón Silver", "Texas")] = new("+5.8%", "+4.2%", 5.1, "On Track",
            "Strong performance in DFW and Houston on-premise. HEB and Spec's pulling well. Casamigos still leads in velocity per point of distribution but Patrón closing the gap in 750ml."),
        [("Patrón Silver", "California")] = new("-1.3%", "-2.8%", 7.2, "Declining",
            "Celebrity tequila brands dominating shelf space in Southern California. Clase Azul displays displacing Patrón in premium end-caps at Total Wine. Bay Area on-premise holding steady."),
        [("Patrón Silver", "National")] = new("+1.9%", "-0.5%", 6.8, "On Track",
            "National trends show flat to slight growth. Losing share in the ultra-premium segment to Clase Azul and Don Julio 1942, but holding in core premium. E-commerce channel up 14%."),

        // ── Patrón Reposado ────────────────────────────────────────
        [("Patrón Reposado", "Texas")] = new("+8.4%", "+6.1%", 4.3, "Growth Leader",
            "Benefiting from the sipping tequila trend in Texas. Outpacing Casamigos Reposado in on-premise velocity. Barrel-aged cocktail menus driving trial in Austin and San Antonio."),
        [("Patrón Reposado", "New York")] = new("+3.2%", "+1.8%", 5.9, "On Track",
            "Steady growth in Manhattan and Brooklyn craft cocktail accounts. Competing well against Fortaleza Reposado at the $50-60 price tier. Long Island retail underperforming."),
        [("Patrón Reposado", "California")] = new("+0.7%", "-1.2%", 6.5, "On Track",
            "Flat in off-premise but growing in on-premise tasting rooms and high-end Mexican restaurants. Consumers trading up from Patrón Silver in the repo category."),

        // ── Patrón Añejo ───────────────────────────────────────────
        [("Patrón Añejo", "Florida")] = new("+4.6%", "+3.9%", 4.8, "On Track",
            "Growing steadily as aged tequila gains share from bourbon among 35-50 demographic. Winter seasonal lift from snowbird population. Competing directly with Don Julio Añejo."),
        [("Patrón Añejo", "National")] = new("+6.1%", "+5.3%", 4.1, "Growth Leader",
            "Aged tequila segment growing 18% nationally. Patrón Añejo capturing disproportionate share of trade-up from reposado. Gift set SKU performing exceptionally in Q4."),

        // ── Angel's Envy ───────────────────────────────────────────
        [("Angel's Envy", "New York")] = new("+12.3%", "+8.7%", 3.2, "Growth Leader",
            "Strong pull in Manhattan craft cocktail bars. Allocation concerns emerging — three key accounts threatening to delist if supply isn't stabilized by Q3. Port-finished profile resonating with wine-crossover consumers."),
        [("Angel's Envy", "Illinois")] = new("+9.1%", "+7.4%", 3.8, "Growth Leader",
            "Chicago's cocktail renaissance driving demand. Binny's featuring in holiday displays. Outperforming Woodford Reserve Double Oaked in the $50-60 American whiskey tier. Limited editions selling out in hours."),
        [("Angel's Envy", "Georgia")] = new("+6.8%", "+4.2%", 4.5, "On Track",
            "Atlanta on-premise strong but rural Georgia distribution gaps persist. Competing with Maker's Mark Private Select for barrel program accounts. Total Wine velocity up 22%."),
        [("Angel's Envy", "National")] = new("+10.5%", "+7.9%", 3.5, "Growth Leader",
            "Fastest-growing bourbon in the Bacardi portfolio. Supply constraints limiting upside — could grow 15%+ with full allocation. E-commerce waitlists generating organic buzz."),

        // ── Bacardi Superior ───────────────────────────────────────
        [("Bacardi Superior", "Florida")] = new("+1.2%", "+0.8%", 6.2, "On Track",
            "Core brand holding steady in home market. Mojito occasion driving summer velocity. Losing some share to Flor de Caña and Real McCoy in the craft rum segment but maintaining volume leadership."),
        [("Bacardi Superior", "National")] = new("-0.4%", "-1.1%", 7.0, "Declining",
            "Rum category under pressure from tequila and RTD growth. Value segment holding but premium rum consumers migrating to aged expressions. 1.75L SKU still the volume driver."),
        [("Bacardi Superior", "Georgia")] = new("+2.4%", "+1.6%", 5.5, "On Track",
            "Solid performance in Atlanta metro, particularly in the Hispanic consumer segment. College market (Athens, Savannah) showing seasonal strength. Captain Morgan remains primary competitor."),

        // ── Bacardi Gold ───────────────────────────────────────────
        [("Bacardi Gold", "Florida")] = new("-2.1%", "-3.5%", 8.8, "Overstocked",
            "Consumers trading up to Bacardi Reserva Ocho or switching to aged tequila. Gold segment declining across the rum category. Need promotional support to move excess inventory before Q3."),
        [("Bacardi Gold", "Texas")] = new("-1.8%", "-2.4%", 7.5, "Declining",
            "Struggling against the tequila wave in Texas. Gold rum cocktails losing menu placements to margarita and paloma variations. Price promotions showing diminishing returns."),

        // ── Grey Goose ─────────────────────────────────────────────
        [("Grey Goose", "California")] = new("-6.2%", "-3.1%", 11.0, "Overstocked",
            "Premium vodka losing share to celebrity tequila brands. Younger consumers (25-34) actively moving away from vodka category. Espresso Martini trend providing some tailwind but not enough to offset declines."),
        [("Grey Goose", "New York")] = new("-2.8%", "-1.5%", 8.3, "Declining",
            "Still strong in bottle service and nightlife but losing daytime cocktail occasions to gin and tequila. Tito's undercutting on price in off-premise. Hamptons summer season was flat YoY."),
        [("Grey Goose", "Florida")] = new("-4.1%", "-2.7%", 9.5, "Overstocked",
            "South Beach bottle service holding but overall Miami market shifting to tequila and mezcal. Belvedere gaining share in the super-premium vodka space. Need to reduce pipeline inventory."),
        [("Grey Goose", "National")] = new("-3.9%", "-2.2%", 9.1, "Declining",
            "Vodka category declining 4% nationally in premium tier. Grey Goose holding share within vodka but losing occasions to other spirits. Flavored extensions (Essences line) underperforming expectations."),
        [("Grey Goose", "Illinois")] = new("-1.4%", "-0.8%", 7.8, "On Track",
            "Chicago holding better than national trends. Martini culture in River North and Gold Coast sustaining velocity. Winter cocktail menus helping. Costco 1.75L driving volume."),

        // ── Bombay Sapphire ────────────────────────────────────────
        [("Bombay Sapphire", "New York")] = new("+3.5%", "+2.8%", 5.2, "On Track",
            "Gin & Tonic resurgence in NYC on-premise. Competing well against Hendrick's and Aviation. New Bombay Bramble driving trial among younger consumers. Brooklyn cocktail bars featuring in seasonal menus."),
        [("Bombay Sapphire", "California")] = new("+1.2%", "+0.4%", 6.1, "On Track",
            "Steady in NorCal, flat in SoCal. Craft gin proliferation creating headwinds — Empress and St. George taking premium shelf space. G&T still the core serve driving volume."),
        [("Bombay Sapphire", "National")] = new("+2.1%", "+1.4%", 5.8, "On Track",
            "Gin category stable with slight growth. Bombay Sapphire maintaining #2 position behind Tanqueray. Premier Cru extension gaining traction in travel retail and gifting occasions."),

        // ── Cazadores Blanco ───────────────────────────────────────
        [("Cazadores Blanco", "Texas")] = new("+14.2%", "+11.8%", 3.0, "Growth Leader",
            "Explosive growth in the value-premium tequila segment. Hispanic consumer base expanding distribution. Outperforming Espolòn and Olmeca Altos at the $22-28 price point. HEB velocity up 34%."),
        [("Cazadores Blanco", "California")] = new("+9.7%", "+7.3%", 3.8, "Growth Leader",
            "Strong momentum in LA and San Diego markets driven by the Hispanic demographic. Gaining share from Jose Cuervo Tradicional. On-premise margarita pours increasing at chain restaurants."),
        [("Cazadores Blanco", "National")] = new("+7.5%", "+5.9%", 4.2, "Growth Leader",
            "Best value-premium tequila story in the portfolio. Distribution expansion opportunity — only 45% ACV nationally vs 78% for Espolòn. Every new distribution point converts at above-average velocity."),

        // ── Cazadores Reposado ─────────────────────────────────────
        [("Cazadores Reposado", "Texas")] = new("+10.6%", "+8.2%", 3.5, "Growth Leader",
            "Sipping occasion growing among Hispanic consumers. Outpacing the blanco expression in velocity growth. Replacing Don Julio Reposado as the house pour in several Tex-Mex chain accounts."),
        [("Cazadores Reposado", "Georgia")] = new("+5.3%", "+3.1%", 5.0, "On Track",
            "Atlanta's growing Latino population driving organic demand. Distribution still light outside metro — opportunity in Savannah and Augusta. Package store owners requesting POS materials."),

        // ── Dewar's 12 ────────────────────────────────────────────
        [("Dewar's 12", "New York")] = new("-3.4%", "-4.8%", 9.2, "Overstocked",
            "Blended Scotch under pressure from single malts and American whiskey. Losing placements to Monkey Shoulder and Johnnie Walker Black. Asian restaurant channel (Scotch & sushi) still performing."),
        [("Dewar's 12", "Florida")] = new("-1.9%", "-2.3%", 7.8, "Declining",
            "Snowbird demographic still loyal but shrinking. On-premise Scotch pours declining in favor of bourbon and tequila. Dewar's 15 and 18 trading up some consumers but net category is down."),
        [("Dewar's 12", "Illinois")] = new("-0.5%", "-1.0%", 6.9, "On Track",
            "Chicago Scotch culture providing stability. Performing in steakhouse and upscale dining accounts. Japanese highball trend creating some incremental occasions. Need to defend against Nikka and Suntory."),
        [("Dewar's 12", "National")] = new("-2.1%", "-3.2%", 8.0, "Declining",
            "Blended Scotch category declining 5% nationally. Dewar's outperforming category but still negative. Trade-up strategy to Dewar's 15 showing promise — need to accelerate."),

        // ── St-Germain ─────────────────────────────────────────────
        [("St-Germain", "New York")] = new("+7.8%", "+6.5%", 4.0, "Growth Leader",
            "Elderflower cocktails ubiquitous in Manhattan. Hugo Spritz trend driving summer velocity. Key ingredient in brunch cocktail culture. Competing against house-made syrups in craft bars."),
        [("St-Germain", "California")] = new("+5.4%", "+4.1%", 4.8, "On Track",
            "Strong in the wellness-adjacent cocktail movement. Low-ABV and spritz occasions growing. Napa and Sonoma wine country bars using extensively. Limited seasonality — year-round performer."),
        [("St-Germain", "National")] = new("+6.2%", "+4.8%", 4.5, "Growth Leader",
            "Liqueur category bright spot. Distribution still underpenetrated — 52% ACV nationally. Every incremental display drives measurable lift. Social media cocktail content generating organic demand."),
    };

    // ── Shipment Data (Three-Tier Tension) ───────────────────────────────
    // Shipments = Bacardi → Distributor ("Sell-In")
    // The "wow" anomaly: Shipments UP but Sell-Through DOWN = Pipeline Clog
    private static readonly Dictionary<(string Brand, string Region), ShipmentRecord> ShipmentData = new(StringTupleComparer.Instance)
    {
        // ── Patrón Silver — THE PIPELINE CLOG SCENARIO ─────────────
        [("Patrón Silver", "Florida")] = new("+5.2%", "-3.0%", "+2.1%", 8.5, 12_400, 9_800,
            "pipeline_clog", "high",
            "Shipments to Southern Glazer's are up 5.2%, but actual consumer sell-through is DOWN 3.0%. Distributor warehouses in Jacksonville and Tampa holding 12,400 cases vs 9,800 depletions — a 2,600 case gap widening each month. The $59 price point is meeting resistance from Casamigos at $52. Retailers demanding markdown support."),
        [("Patrón Silver", "Texas")] = new("+4.8%", "+4.2%", "+5.8%", 5.1, 18_200, 17_500,
            "healthy", "low",
            "Shipments and sell-through aligned in Texas. Republic National pulling well. HEB reorders consistent. No pipeline concerns — the market is genuinely consuming what's being shipped."),
        [("Patrón Silver", "California")] = new("+3.1%", "-2.8%", "-1.3%", 7.2, 15_600, 11_200,
            "pipeline_clog", "medium",
            "Shipping ahead of demand in SoCal. Total Wine and BevMo warehouses accumulating excess. Bay Area pulling better but LA/OC is building inventory. Celebrity tequila brands stealing velocity."),
        [("Patrón Silver", "National")] = new("+4.5%", "-0.5%", "+1.9%", 6.8, 142_000, 128_500,
            "pipeline_building", "medium",
            "National shipments outpacing sell-through by 5 points. Not yet critical but trend is concerning. Q4 holiday pipeline fill may mask the issue until Q1 correction. Monitoring closely."),

        // ── Angel's Envy — SUPPLY CONSTRAINT (opposite problem) ────
        [("Angel's Envy", "New York")] = new("-8.2%", "+8.7%", "+12.3%", 3.2, 2_100, 3_800,
            "supply_constraint", "high",
            "Cannot ship enough to meet demand. Allocation cuts have reduced shipments 8.2% while consumer demand grows 8.7%. Three key Manhattan accounts on allocation waitlists. Lost 2 accounts to Woodford Reserve who can supply."),
        [("Angel's Envy", "National")] = new("-5.1%", "+7.9%", "+10.5%", 3.5, 22_000, 31_500,
            "supply_constraint", "critical",
            "National demand exceeds supply by ~30%. Every case shipped depletes within 2 weeks. E-commerce waitlists at 4,500+ consumers. Distillery expansion won't come online until 2027. Strategic allocation decisions needed NOW."),

        // ── Grey Goose — CATEGORY DECLINE ──────────────────────────
        [("Grey Goose", "California")] = new("-2.1%", "-3.1%", "-6.2%", 11.0, 8_900, 5_400,
            "pipeline_clog", "critical",
            "Still shipping too much despite cutting 2.1%. Consumer demand falling 3x faster than shipment reductions. SoCal distributor warehouses at 11 weeks. Espresso Martini trend is the only tailwind. Urgent: need 25% shipment reduction for Q3."),
        [("Grey Goose", "New York")] = new("-1.5%", "-1.5%", "-2.8%", 8.3, 7_200, 6_100,
            "declining_aligned", "medium",
            "Shipments and sell-through both declining at similar rates — pipeline is stable but the floor is dropping. Bottle service accounts holding but losing daytime cocktail occasions to gin and tequila."),
        [("Grey Goose", "Florida")] = new("-0.8%", "-2.7%", "-4.1%", 9.5, 6_800, 4_900,
            "pipeline_clog", "high",
            "Shipment cuts not keeping pace with demand decline. Miami-Dade at 12+ weeks inventory. South Beach accounts stable but suburban off-premise collapsing. Need aggressive depletion programs to clear pipeline."),

        // ── Bacardi Superior — STABLE VOLUME ──────────────────────
        [("Bacardi Superior", "Florida")] = new("+1.5%", "+0.8%", "+1.2%", 6.2, 24_000, 22_800,
            "healthy", "low",
            "Shipments and depletions well-aligned in the home market. Mojito season driving consistent pull. Pipeline healthy with manageable weeks on hand."),
        [("Bacardi Superior", "National")] = new("-0.2%", "-1.1%", "-0.4%", 7.0, 185_000, 172_000,
            "declining_aligned", "low",
            "Gentle decline across both shipments and sell-through. Pipeline not building but the category is under structural pressure from tequila. Volume management appropriate."),

        // ── Cazadores Blanco — GROWTH OPPORTUNITY ──────────────────
        [("Cazadores Blanco", "Texas")] = new("+18.5%", "+11.8%", "+14.2%", 3.0, 8_500, 7_900,
            "growth_opportunity", "low",
            "Shipping aggressively to fill new distribution — but sell-through is keeping pace. Republic National requesting more allocation. HEB and Spec's expanding shelf space. Healthy growth pipeline."),
        [("Cazadores Blanco", "National")] = new("+12.1%", "+5.9%", "+7.5%", 4.2, 45_000, 38_000,
            "pipeline_building", "medium",
            "Shipping ahead of sell-through as distribution expands from 45% to projected 55% ACV. Expected pipeline fill — monitoring to ensure new points of distribution convert."),

        // ── Bombay Sapphire ────────────────────────────────────────
        [("Bombay Sapphire", "New York")] = new("+2.8%", "+2.8%", "+3.5%", 5.2, 5_400, 5_200,
            "healthy", "low",
            "Shipments and sell-through perfectly aligned. G&T resurgence sustaining healthy pull. Pipeline well-managed."),
        [("Bombay Sapphire", "National")] = new("+1.8%", "+1.4%", "+2.1%", 5.8, 62_000, 58_500,
            "healthy", "low",
            "Category stability reflected in balanced shipments. No pipeline concerns. Bramble extension adding incremental volume without disrupting core."),

        // ── Dewar's 12 — MANAGED DECLINE ──────────────────────────
        [("Dewar's 12", "National")] = new("-3.5%", "-3.2%", "-2.1%", 8.0, 28_000, 24_500,
            "declining_aligned", "medium",
            "Shipments declining faster than depletions — intentionally reducing pipeline to right-size inventory. Dewar's 15 trade-up absorbing some volume. Category decline managed but not reversed."),

        // ── St-Germain ─────────────────────────────────────────────
        [("St-Germain", "New York")] = new("+9.2%", "+6.5%", "+7.8%", 4.0, 3_200, 2_900,
            "growth_opportunity", "low",
            "Shipping ahead to fill seasonal demand (Hugo Spritz summer). Pipeline healthy — every case converts within 3 weeks. Distribution white space still significant."),
        [("St-Germain", "National")] = new("+8.5%", "+4.8%", "+6.2%", 4.5, 18_500, 15_200,
            "pipeline_building", "low",
            "Distribution expansion from 52% to target 62% ACV driving shipment-over-depletion gap. Expected and healthy pipeline fill. Monitoring conversion rates at new points of distribution."),
    };

    private static readonly Dictionary<(string Brand, string Region), string> SentimentData = new(StringTupleComparer.Instance)
    {
        // ── Patrón Silver ──────────────────────────────────────────
        [("Patrón Silver", "Florida")] = "Distributors report consumer shift toward Tequila Ocho at the $45 price point. On-premise velocity in Miami remains high — South Beach accounts up 6% — but suburban retail in Tampa and Jacksonville is lagging significantly. Costco depletions down 11% vs prior year on the 1.75L. Retailers requesting deeper TPR support to compete with Casamigos holiday displays. Off-premise facings shrinking from 4 to 3 in several Publix resets.",

        [("Patrón Silver", "Texas")] = "HEB and Spec's pulling well with consistent velocity. DFW on-premise showing strength in upscale Mexican restaurants. Casamigos still leads in velocity per point of distribution but Patrón is closing the gap on the 750ml SKU. Total Wine featuring in margarita season displays. Distributors note strong brand loyalty among 35-50 age group. Concern about Clase Azul Plata stealing the gifting occasion.",

        [("Patrón Silver", "California")] = "Celebrity tequila brands are dominating shelf space in Southern California. Clase Azul displays are displacing Patrón in premium end-caps at Total Wine and BevMo. Bay Area on-premise is holding steady thanks to established cocktail programs. LA nightlife shifting to Lobos 1707 and 818 Tequila. Distributors report 40% of new placements going to brands launched in the last 3 years. Need to defend core shelf position.",

        [("Patrón Silver", "National")] = "National trends show flat to slight growth. The brand is losing share in the ultra-premium segment ($50+) to Clase Azul and Don Julio 1942 but holding well in the core premium tier ($40-50). E-commerce channel via Drizly and ReserveBar up 14%. RTD Patrón Margarita canned cocktail generating incremental occasions without significant cannibalization of the base spirit.",

        // ── Patrón Reposado ────────────────────────────────────────
        [("Patrón Reposado", "Texas")] = "Sipping tequila trend is a massive tailwind in Texas. Barrel-aged cocktail menus in Austin and San Antonio driving trial among bourbon converts. Outpacing Casamigos Reposado 2:1 in on-premise velocity at accounts that carry both. Distributor reps report the Patrón Reposado is the #1 requested back-bar tequila for Old Fashioned variations. Supply comfortable — no allocation issues.",

        [("Patrón Reposado", "New York")] = "Steady growth in Manhattan and Brooklyn craft cocktail accounts. Competing well against Fortaleza Reposado at the $50-60 price tier. Long Island retail underperforming — consumer preference skewing toward value brands in suburban off-premise. SoHo and West Village accounts reporting Patrón Reposado as a top-5 spirit by velocity. Distributor sentiment positive.",

        // ── Patrón Añejo ───────────────────────────────────────────
        [("Patrón Añejo", "Florida")] = "Growing steadily as aged tequila gains share from bourbon among 35-50 demographic. Winter seasonal lift from snowbird population spending December-March. South Florida steakhouses adding Patrón Añejo to whiskey menus. Competing directly with Don Julio Añejo at similar price points. Retailers report strong gift box sales in Q4. Consumer education on sipping tequila still needed in Central Florida.",

        [("Patrón Añejo", "National")] = "Aged tequila segment growing 18% nationally — fastest-growing sub-segment in spirits. Patrón Añejo capturing disproportionate share of trade-up from reposado. Gift set SKU performing exceptionally in Q4, accounting for 22% of annual volume. On-premise tasting flights pairing añejo with bourbon driving category conversion. Extra Añejo extension opportunity being evaluated.",

        // ── Angel's Envy ───────────────────────────────────────────
        [("Angel's Envy", "New York")] = "Strong pull in Manhattan craft cocktail bars — bartenders actively hand-selling the port-finished profile to wine-crossover consumers. Allocation concerns are real: three key accounts (Death & Co, Attaboy, Employees Only) threatening to delist if supply isn't stabilized by Q3. Brooklyn bottle shops can't keep the Rye expression in stock. Competing favorably against Woodford Reserve Double Oaked and Maker's Mark 46. Need 20% more allocation to capture full demand.",

        [("Angel's Envy", "Illinois")] = "Chicago's cocktail renaissance is a perfect fit for the brand. Binny's featuring in holiday displays with prominent end-cap placement. Outperforming Woodford Reserve Double Oaked in the $50-60 American whiskey tier at measured accounts. Limited editions (Cellar Collection) selling out within hours of release. On-premise velocity in River North and Wicker Park up 30% YoY. Supply is the only constraint on growth.",

        [("Angel's Envy", "Georgia")] = "Atlanta on-premise performing well, particularly in Buckhead and Midtown steakhouse accounts. Rural Georgia distribution gaps persist — only 35% ACV outside metro Atlanta. Competing with Maker's Mark Private Select for barrel program partnerships. Total Wine velocity up 22% driven by in-store tastings. Savannah and Augusta represent untapped opportunity. Consumer awareness still building in the Southeast.",

        [("Angel's Envy", "National")] = "Fastest-growing bourbon in the Bacardi portfolio with double-digit growth for the fourth consecutive year. Supply constraints are the primary limiter — could grow 15%+ with full allocation across all markets. E-commerce waitlists on the brand website generating organic buzz and social media content. The port-finished positioning creates a unique niche with no direct competitor at scale. Trade-up to Rye expression adding incremental revenue.",

        // ── Bacardi Superior ───────────────────────────────────────
        [("Bacardi Superior", "Florida")] = "Core brand holding steady in the home market where Bacardi has deep cultural roots. Mojito occasion driving summer velocity — Miami bars report Bacardi Mojito as the #1 cocktail by volume May-September. Losing some share to Flor de Caña and Real McCoy in the emerging craft rum segment, but these are small-volume competitors. Maintaining volume leadership in white rum by a wide margin over Captain Morgan White and Malibu.",

        [("Bacardi Superior", "National")] = "Rum category under structural pressure from tequila and RTD growth. The value segment is holding thanks to price-conscious consumers, but premium rum drinkers are migrating to aged expressions (Bacardi Reserva Ocho, Bacardi Diez). The 1.75L SKU remains the volume driver, accounting for 55% of total depletions. Need to defend against Tito's encroachment in the 'default spirit' occasion.",

        [("Bacardi Superior", "Georgia")] = "Solid performance in Atlanta metro driven by the Hispanic consumer segment and college markets. Savannah's tourist-driven bar scene provides seasonal lift. Captain Morgan White Rum is the primary competitor in the value tier. Package store owners in suburban Atlanta requesting bilingual POS materials. Growth opportunity in the RTD Bacardi cocktail cans to capture convenience occasions.",

        // ── Bacardi Gold ───────────────────────────────────────────
        [("Bacardi Gold", "Florida")] = "Consumers actively trading up to Bacardi Reserva Ocho or switching out of rum entirely to aged tequila. The gold rum segment is declining across the total category — not a Bacardi-specific issue. Excess inventory needs to move before Q3 to avoid markdown pressure. Retailers requesting promotional pricing to clear shelves. Cuba Libre occasion declining among younger consumers who view it as dated.",

        [("Bacardi Gold", "Texas")] = "The tequila wave in Texas is directly cannibalizing gold rum occasions. Dark spirit cocktails that used to default to rum (Dark & Stormy, Mai Tai) are losing menu placements to margarita and paloma variations. Price promotions showing diminishing returns — each successive TPR cycle generates less incremental volume. Need a strategic review of the Gold SKU's role in the Texas market.",

        // ── Grey Goose ─────────────────────────────────────────────
        [("Grey Goose", "California")] = "Premium vodka category is in structural decline in California as younger consumers (25-34) actively move away from vodka. Celebrity tequila brands capturing the 'status spirit' occasion that Grey Goose once owned. Espresso Martini trend providing some tailwind but insufficient to offset broader declines. Tito's undercutting on price in the off-premise channel. Need to reposition around cocktail culture rather than bottle service occasions.",

        [("Grey Goose", "New York")] = "Still strong in bottle service and nightlife accounts in Manhattan — Marquee, 1Oak, and Lavo still pouring significant volume. However, losing daytime cocktail occasions to gin (Hendrick's, Aviation) and tequila (Casamigos, Don Julio). Tito's aggressively undercutting on price in Brooklyn and Queens off-premise. Hamptons summer season was flat YoY for the first time in a decade. Need innovation pipeline.",

        [("Grey Goose", "Florida")] = "South Beach bottle service holding at legacy accounts but new venue openings overwhelmingly defaulting to tequila programs. Belvedere gaining share in the super-premium vodka space with aggressive mixologist partnerships. Miami-Dade inventory is 12+ weeks — significantly overstocked. Need to reduce pipeline before the slow September-November period. Tampa and Orlando performing closer to national averages.",

        [("Grey Goose", "National")] = "Vodka category declining 4% nationally in the premium tier. Grey Goose is holding share within vodka (still #1 premium) but losing occasions to other spirit categories. The Essences flavored line underperformed expectations — only achieving 40% of year-one volume targets. Positive signal: the brand still indexes highest in consumer luxury perception surveys. Need to translate brand equity into cocktail occasions.",

        [("Grey Goose", "Illinois")] = "Chicago is outperforming national trends for Grey Goose. Martini culture in River North and Gold Coast is sustaining on-premise velocity. Winter cocktail menus featuring Grey Goose Espresso Martini helping. Costco 1.75L driving off-premise volume in the suburbs. The Midwest consumer appears less susceptible to the tequila shift than coastal markets. Defend this market as a stronghold.",

        // ── Bombay Sapphire ────────────────────────────────────────
        [("Bombay Sapphire", "New York")] = "The gin & tonic resurgence in NYC on-premise is a tailwind. Competing well against Hendrick's in the $30-35 price tier and Aviation in cocktail-forward accounts. The new Bombay Bramble (blackberry & raspberry) is driving trial among consumers who find London Dry too botanical. Brooklyn cocktail bars featuring in seasonal spring/summer menus. Monkey Shoulder crossover occasions being monitored.",

        [("Bombay Sapphire", "California")] = "Steady in NorCal wine country where G&T is an aperitif staple, but flat in SoCal. Craft gin proliferation (Empress, St. George, Cutwater) creating headwinds in the artisanal channel. The core G&T serve remains the volume driver. Need to defend against Tanqueray 10 in the super-premium space. Trader Joe's house gin brand is also an emerging threat in the value tier.",

        [("Bombay Sapphire", "National")] = "Gin category stable with 2% growth nationally. Bombay Sapphire maintaining the #2 position behind Tanqueray in the premium tier. Premier Cru extension gaining traction in travel retail and gifting occasions — 15% of volume in its first year. Negroni and Martini trends on social media driving awareness among younger consumers. Distribution is mature at 82% ACV.",

        // ── Cazadores Blanco ───────────────────────────────────────
        [("Cazadores Blanco", "Texas")] = "Explosive growth story in the value-premium tequila segment ($22-28). Hispanic consumer base is the foundation, with crossover appeal growing among general market. Outperforming Espolòn and Olmeca Altos at the same price point. HEB velocity up 34% driven by prominent shelf placement and bilingual POS. On-premise gaining margarita pours at Chili's, On The Border, and other Tex-Mex chains. Distribution still expanding — significant upside remains.",

        [("Cazadores Blanco", "California")] = "Strong momentum in LA and San Diego driven by the large Hispanic demographic. Gaining share from Jose Cuervo Tradicional among consumers trading up from mixto. On-premise margarita pours increasing at both independent and chain restaurants. Distributors report Cazadores as the fastest-turning tequila under $30 in Southern California. NorCal distribution is lighter — opportunity in Sacramento and San Jose.",

        [("Cazadores Blanco", "National")] = "Best value-premium tequila growth story in the portfolio. Only at 45% ACV nationally vs 78% for Espolòn — massive distribution white space. Every new distribution point converts at above-average velocity, suggesting latent demand. The brand over-indexes with Hispanic consumers but crossover appeal is growing fast. A dedicated Hispanic marketing investment would accelerate what is already organic momentum.",

        // ── Cazadores Reposado ─────────────────────────────────────
        [("Cazadores Reposado", "Texas")] = "Sipping occasion growing among Hispanic consumers, especially 30-45 males. Outpacing the blanco expression in velocity growth, which is unusual for the category. Replacing Don Julio Reposado as the house pour in several Tex-Mex chain accounts due to better margin for operators. Retail shelf placement next to Espolòn Reposado is driving comparison shopping in Cazadores' favor.",

        [("Cazadores Reposado", "Georgia")] = "Atlanta's growing Latino population is driving organic demand without significant marketing support. Distribution is still light outside metro Atlanta — major opportunity in Savannah, Augusta, and Columbus. Package store owners are actively requesting POS materials and display racks. Word-of-mouth within Hispanic communities is the primary demand driver. Need field sales coverage to capitalize.",

        // ── Dewar's 12 ────────────────────────────────────────────
        [("Dewar's 12", "New York")] = "Blended Scotch under significant pressure from single malts (Macallan, Glenlivet) and American whiskey (Angel's Envy, Woodford). Losing on-premise placements to Monkey Shoulder, which bartenders view as more versatile for cocktails. Asian restaurant channel (Scotch with sushi/Japanese cuisine) is a bright spot. The 35+ male consumer remains loyal but is not being replaced by younger drinkers. Dewar's 15 trade-up is the strategic play.",

        [("Dewar's 12", "Florida")] = "Snowbird demographic (65+) still loyal but this consumer base is shrinking naturally. On-premise Scotch pours declining across the state in favor of bourbon and tequila. Dewar's 15 and 18 are trading up some consumers but the net category impact is still negative. Golf club accounts remain a stronghold. Need to find a way to attract the 40-55 professional demographic before the loyal base ages out.",

        [("Dewar's 12", "Illinois")] = "Chicago's Scotch culture — rooted in steakhouse dining and gentleman's club traditions — is providing stability. Performing well in premium steakhouse accounts (Gibson's, RPM Steak, Maple & Ash). The Japanese highball trend is creating incremental occasions at izakaya and ramen restaurants. Need to defend against Nikka and Suntory Toki, which are taking the highball occasion. Binny's shelf space stable.",

        [("Dewar's 12", "National")] = "Blended Scotch category declining 5% nationally — a structural headwind. Dewar's is outperforming the category average (declining only 2.1%) but still negative. The trade-up strategy to Dewar's 15 is showing promise with that SKU growing 8%. Need to accelerate the premiumization narrative while defending the 12-year-old's volume base. Scotch education events at retail showing positive ROI.",

        // ── St-Germain ─────────────────────────────────────────────
        [("St-Germain", "New York")] = "Elderflower cocktails are ubiquitous in Manhattan — the Hugo Spritz is the drink of the summer. Key ingredient in NYC's brunch cocktail culture, appearing on 70%+ of upscale brunch menus. Competing against house-made elderflower syrups in some craft bars, but the consistent quality and brand recognition win in volume accounts. Social media content (Instagram, TikTok) driving organic consumer pull. Year-round velocity with summer peak.",

        [("St-Germain", "California")] = "Strong in the wellness-adjacent cocktail movement — low-ABV and spritz occasions growing 25% YoY. Napa and Sonoma wine country bars using extensively as a bridge between wine and cocktails. LA's health-conscious consumer base gravitating toward lighter, botanical-forward drinks. Limited seasonality compared to other markets — essentially a year-round performer in California. Distribution opportunity in Central Valley.",

        [("St-Germain", "National")] = "The liqueur category's brightest spot. Distribution still underpenetrated at 52% ACV nationally — significant white space in suburban and secondary markets. Every incremental end-cap display drives measurable lift (avg 35% velocity increase during display period). Social media cocktail content generating organic demand that field sales can convert. The Spritz occasion is the growth engine — need to own it the way Aperol owns the Aperol Spritz.",
    };

    public static object GetDepletionStats(string brand, string region, string period)
    {
        var key = (brand.Trim(), region.Trim());

        if (!DepletionData.TryGetValue(key, out var record))
        {
            // Try partial matching with diacritics normalization
            var normalizedBrand = NormalizeDiacritics(brand.Trim());
            var match = DepletionData.Keys.FirstOrDefault(k =>
                NormalizeDiacritics(k.Brand).Contains(normalizedBrand, StringComparison.OrdinalIgnoreCase) &&
                k.Region.Contains(region.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match != default)
            {
                key = match;
                record = DepletionData[match];
            }
            else
                return new { error = $"No data found for brand '{brand}' in region '{region}'.", available_brands = GetAvailableBrands(), available_regions = GetAvailableRegions() };
        }

        var periodMultiplier = period?.Trim().ToUpperInvariant() switch
        {
            "Q1" => 0.85,
            "Q2" => 1.05,
            "Q3" => 0.95,
            "Q4" => 1.15,
            _ => 1.0 // YTD
        };

        return new
        {
            brand = key.Item1,
            region = key.Item2,
            period = period ?? "YTD",
            metrics = new
            {
                depletions_yoy = AdjustPercentage(record.DepletionsYoY, periodMultiplier),
                sell_through_yoy = AdjustPercentage(record.SellThroughYoY, periodMultiplier),
                inventory_weeks_on_hand = Math.Round(record.InventoryWeeks * (2.0 - periodMultiplier), 1),
                status = record.Status
            },
            sentiment_summary = record.SentimentSummary
        };
    }

    public static object GetShipmentStats(string brand, string region, string period)
    {
        var key = (brand.Trim(), region.Trim());

        if (!ShipmentData.TryGetValue(key, out var record))
        {
            // Try partial matching with diacritics normalization
            var normalizedBrand = NormalizeDiacritics(brand.Trim());
            var normalizedRegion = region.Trim();
            var match = ShipmentData.Keys.FirstOrDefault(k =>
                NormalizeDiacritics(k.Brand).Contains(normalizedBrand, StringComparison.OrdinalIgnoreCase) &&
                k.Region.Contains(normalizedRegion, StringComparison.OrdinalIgnoreCase));

            if (match != default)
            {
                key = match;
                record = ShipmentData[match];
            }
            else
                return new { error = $"No shipment data for brand '{brand}' in region '{region}'.", available_brands = GetAvailableBrands(), available_regions = GetAvailableRegions() };
        }

        var periodMultiplier = period?.Trim().ToUpperInvariant() switch
        {
            "Q1" => 0.85,
            "Q2" => 1.05,
            "Q3" => 0.95,
            "Q4" => 1.15,
            _ => 1.0
        };

        return new
        {
            brand = key.Item1,
            region = key.Item2,
            period = period ?? "YTD",
            shipments = new
            {
                shipments_yoy = AdjustPercentage(record.ShipmentsYoY, periodMultiplier),
                sell_through_yoy = AdjustPercentage(record.SellThroughYoY, periodMultiplier),
                depletions_yoy = AdjustPercentage(record.DepletionsYoY, periodMultiplier),
                inventory_weeks_on_hand = Math.Round(record.InventoryWeeks * (2.0 - periodMultiplier), 1),
                cases_shipped = (int)(record.CasesShipped * periodMultiplier),
                cases_depleted = (int)(record.CasesDepleted * periodMultiplier)
            },
            anomaly = new
            {
                type = record.AnomalyType,
                risk_level = record.RiskLevel,
                is_pipeline_clog = record.AnomalyType == "pipeline_clog",
                shipment_depletion_gap = record.CasesShipped - record.CasesDepleted
            },
            analysis = record.Analysis
        };
    }

    public static object GetFieldSentiment(string brand, string region)
    {
        var key = (brand.Trim(), region.Trim());

        if (!SentimentData.TryGetValue(key, out var sentiment))
        {
            var normalizedBrand = NormalizeDiacritics(brand.Trim());
            var match = SentimentData.Keys.FirstOrDefault(k =>
                NormalizeDiacritics(k.Brand).Contains(normalizedBrand, StringComparison.OrdinalIgnoreCase) &&
                k.Region.Contains(region.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match != default)
            {
                key = match;
                sentiment = SentimentData[match];
            }
            else
                return new { error = $"No sentiment data for brand '{brand}' in region '{region}'.", available_brands = GetAvailableBrands(), available_regions = GetAvailableRegions() };
        }

        return new
        {
            brand = key.Item1,
            region = key.Item2,
            source = "Field Sales & Distributor Reports",
            reporting_period = "Current YTD",
            sentiment
        };
    }

    private static string AdjustPercentage(string pct, double multiplier)
    {
        if (double.TryParse(pct.TrimEnd('%').TrimStart('+'), out var val))
        {
            var adjusted = Math.Round(val * multiplier, 1);
            return (adjusted >= 0 ? "+" : "") + adjusted + "%";
        }
        return pct;
    }

    private static string[] GetAvailableBrands() =>
    [
        "Patrón Silver", "Patrón Reposado", "Patrón Añejo",
        "Angel's Envy", "Bacardi Superior", "Bacardi Gold",
        "Grey Goose", "Bombay Sapphire", "Cazadores Blanco",
        "Cazadores Reposado", "Dewar's 12", "St-Germain"
    ];

    private static string[] GetAvailableRegions() =>
        ["Florida", "Texas", "California", "New York", "Illinois", "Georgia", "National"];

    private record DepletionRecord(
        string DepletionsYoY,
        string SellThroughYoY,
        double InventoryWeeks,
        string Status,
        string SentimentSummary);

    private record ShipmentRecord(
        string ShipmentsYoY,
        string SellThroughYoY,
        string DepletionsYoY,
        double InventoryWeeks,
        int CasesShipped,
        int CasesDepleted,
        string AnomalyType,   // pipeline_clog, supply_constraint, healthy, declining_aligned, growth_opportunity, pipeline_building
        string RiskLevel,      // low, medium, high, critical
        string Analysis);

    private static string NormalizeDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Brand, string Region)>
    {
        public static readonly StringTupleComparer Instance = new();
        public bool Equals((string Brand, string Region) x, (string Brand, string Region) y) =>
            string.Equals(x.Brand, y.Brand, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Region, y.Region, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Brand, string Region) obj) =>
            HashCode.Combine(obj.Brand.ToUpperInvariant(), obj.Region.ToUpperInvariant());
    }
}
