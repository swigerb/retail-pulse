import { useState, useEffect, useCallback } from 'react';
import { makeStyles, Tab, TabList, Dropdown, Option, Spinner, tokens } from '@fluentui/react-components';
import type { CompetitorPricing, MarketShareEntry, CompetitiveThreat, CompetitorOverview } from '../../types';
import { fetchCompetitorPricing, fetchMarketShare, fetchThreats, fetchCompetitorProfile } from '../../services/competitiveApi';
import PricingGrid from './PricingGrid';
import MarketShareChart from './MarketShareChart';
import ThreatCards from './ThreatCards';
import CompetitorProfile from './CompetitorProfile';

const CATEGORIES = ['All Categories', 'Grills', 'Sauces', 'Accessories', 'Rubs & Seasonings'];
const REGIONS = ['All Regions', 'Northeast', 'Southeast', 'Midwest', 'Southwest', 'West'];

type TabKey = 'overview' | 'pricing' | 'market-share' | 'threats';

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    overflow: 'auto',
    padding: '24px',
    backgroundColor: tokens.colorNeutralBackground1,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: '16px',
    marginBottom: '20px',
    flexWrap: 'wrap',
  },
  title: {
    fontSize: '22px',
    fontWeight: '700',
    color: '#ef4444',
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    letterSpacing: '-0.5px',
  },
  subtitle: {
    fontSize: '12px',
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '1px',
    fontWeight: '500',
  },
  filters: {
    display: 'flex',
    gap: '8px',
    marginLeft: 'auto',
    flexWrap: 'wrap',
  },
  tabList: {
    marginBottom: '20px',
    borderBottom: `1px solid ${tokens.colorNeutralStroke1}`,
  },
  content: {
    flex: 1,
    minHeight: 0,
  },
  loading: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '12px',
    padding: '60px',
    color: tokens.colorNeutralForeground3,
    fontSize: '14px',
  },
  error: {
    padding: '20px',
    borderRadius: '8px',
    backgroundColor: 'rgba(239,68,68,0.1)',
    border: '1px solid rgba(239,68,68,0.3)',
    color: '#fca5a5',
    fontSize: '13px',
  },
  overviewGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(400px, 1fr))',
    gap: '20px',
  },
});

export default function CompetitiveDashboard() {
  const styles = useStyles();
  const [category, setCategory] = useState('All Categories');
  const [region, setRegion] = useState('All Regions');
  const [activeTab, setActiveTab] = useState<TabKey>('overview');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [pricing, setPricing] = useState<CompetitorPricing[]>([]);
  const [marketShare, setMarketShare] = useState<MarketShareEntry[]>([]);
  const [threats, setThreats] = useState<CompetitiveThreat[]>([]);
  const [selectedCompetitor, setSelectedCompetitor] = useState<CompetitorOverview | null>(null);

  const catParam = category === 'All Categories' ? undefined : category;
  const regParam = region === 'All Regions' ? undefined : region;

  const loadData = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [p, m, t] = await Promise.all([
        fetchCompetitorPricing(catParam, regParam),
        fetchMarketShare(catParam, regParam),
        fetchThreats(catParam, regParam),
      ]);
      setPricing(p);
      setMarketShare(m);
      setThreats(t);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load competitive data');
    } finally {
      setLoading(false);
    }
  }, [catParam, regParam]);

  useEffect(() => { loadData(); }, [loadData]);

  const handleViewCompetitor = useCallback(async (name: string) => {
    try {
      const profile = await fetchCompetitorProfile(name);
      setSelectedCompetitor(profile);
    } catch {
      setSelectedCompetitor(null);
    }
  }, []);

  return (
    <div className={styles.container} data-testid="competitive-dashboard">
      <div className={styles.header}>
        <div>
          <div className={styles.title}>⚔️ Competitive Intelligence</div>
          <div className={styles.subtitle}>War Room • Real-Time Market Analysis</div>
        </div>
        <div className={styles.filters}>
          <Dropdown
            data-testid="category-filter"
            value={category}
            selectedOptions={[category]}
            onOptionSelect={(_, data) => setCategory(data.optionValue ?? 'All Categories')}
            size="small"
          >
            {CATEGORIES.map(c => <Option key={c} value={c}>{c}</Option>)}
          </Dropdown>
          <Dropdown
            data-testid="region-filter"
            value={region}
            selectedOptions={[region]}
            onOptionSelect={(_, data) => setRegion(data.optionValue ?? 'All Regions')}
            size="small"
          >
            {REGIONS.map(r => <Option key={r} value={r}>{r}</Option>)}
          </Dropdown>
        </div>
      </div>

      <TabList
        className={styles.tabList}
        selectedValue={activeTab}
        onTabSelect={(_, d) => setActiveTab(d.value as TabKey)}
      >
        <Tab value="overview">📊 Overview</Tab>
        <Tab value="pricing">💰 Pricing</Tab>
        <Tab value="market-share">📈 Market Share</Tab>
        <Tab value="threats">🚨 Threats</Tab>
      </TabList>

      {error && <div className={styles.error} data-testid="error-message">⚠️ {error}</div>}

      {loading ? (
        <div className={styles.loading}>
          <Spinner size="medium" />
          Loading competitive intelligence...
        </div>
      ) : (
        <div className={styles.content}>
          {activeTab === 'overview' && (
            <div className={styles.overviewGrid} data-testid="overview-tab">
              <MarketShareChart data={marketShare} compact />
              <ThreatCards threats={threats.slice(0, 3)} compact />
            </div>
          )}
          {activeTab === 'pricing' && (
            <div data-testid="pricing-tab">
              <PricingGrid data={pricing} />
            </div>
          )}
          {activeTab === 'market-share' && (
            <div data-testid="market-share-tab">
              <MarketShareChart data={marketShare} />
            </div>
          )}
          {activeTab === 'threats' && (
            <div data-testid="threats-tab">
              <ThreatCards threats={threats} onViewCompetitor={handleViewCompetitor} />
            </div>
          )}
          {selectedCompetitor && (
            <CompetitorProfile
              competitor={selectedCompetitor}
              onClose={() => setSelectedCompetitor(null)}
            />
          )}
        </div>
      )}
    </div>
  );
}
