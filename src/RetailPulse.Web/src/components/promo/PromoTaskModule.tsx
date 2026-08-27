import { useState, useCallback } from 'react';
import {
  Button,
  Input,
  Label,
  Slider,
  Spinner,
  makeStyles,
  Card,
  Dropdown,
  Option,
} from '@fluentui/react-components';
import type { PromoType, PromoFormData, PromoEvaluation, PromoCampaign } from '../../types';
import PromoTypeSelector from './PromoTypeSelector';
import PromoRecommendation from './PromoRecommendation';
import PromoCalendar from './PromoCalendar';
import ROIChart from './ROIChart';
import { evaluatePromo, submitForApproval, fetchExistingCampaigns } from '../../services/promoApi';
import { useEffect } from 'react';

// Tenant-level defaults — would come from config in production
const TENANT_BRANDS = ['Apex Grill', 'Coastal Catch', 'Mountain Roast', 'Prairie Farms', 'Urban Bites'];
const TENANT_REGIONS = ['Northeast', 'Southeast', 'Midwest', 'Southwest', 'West Coast', 'National'];

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '24px',
    padding: '24px',
    maxWidth: '960px',
    marginLeft: 'auto',
    marginRight: 'auto',
  },
  titleSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    marginBottom: '4px',
  },
  title: {
    fontSize: '20px',
    fontWeight: '700',
    color: '#22c55e',
    letterSpacing: '-0.3px',
  },
  subtitle: {
    fontSize: '13px',
    color: '#94a3b8',
  },
  formCard: {
    padding: '20px',
    borderRadius: '12px',
    backgroundColor: 'var(--color-surface-alt, rgba(255,255,255,0.02))',
    border: '1px solid rgba(255,255,255,0.06)',
  },
  formGrid: {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: '16px',
    '@media (max-width: 600px)': {
      gridTemplateColumns: '1fr',
    },
  },
  field: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },
  fieldLabel: {
    fontSize: '12px',
    fontWeight: '600',
    color: '#94a3b8',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
  },
  fieldFullWidth: {
    gridColumn: 'span 2',
    '@media (max-width: 600px)': {
      gridColumn: 'span 1',
    },
  },
  sliderRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
  },
  sliderValue: {
    fontSize: '14px',
    fontWeight: '600',
    color: '#22c55e',
    minWidth: '48px',
    textAlign: 'right',
  },
  actions: {
    display: 'flex',
    gap: '12px',
    justifyContent: 'flex-end',
    alignItems: 'center',
    marginTop: '8px',
    flexWrap: 'wrap',
  },
  missingHint: {
    fontSize: '12px',
    color: 'var(--colorNeutralForeground3)',
  },
  evaluateButton: {
    minWidth: '180px',
  },
  loadingOverlay: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '12px',
    padding: '40px',
    borderRadius: '12px',
    backgroundColor: 'rgba(34,197,94,0.04)',
    border: '1px solid rgba(34,197,94,0.15)',
  },
  loadingText: {
    fontSize: '14px',
    color: '#94a3b8',
    fontStyle: 'italic',
  },
  errorBanner: {
    padding: '12px 16px',
    borderRadius: '8px',
    backgroundColor: 'rgba(239,68,68,0.08)',
    border: '1px solid rgba(239,68,68,0.2)',
    color: '#fca5a5',
    fontSize: '13px',
  },
  sectionTitle: {
    fontSize: '13px',
    fontWeight: '600',
    color: '#64748b',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    marginBottom: '8px',
  },
});

export default function PromoTaskModule() {
  const styles = useStyles();
  const [brand, setBrand] = useState('');
  const [region, setRegion] = useState('');
  const [promoType, setPromoType] = useState<PromoType | ''>('');
  const [budget, setBudget] = useState('');
  const [startDate, setStartDate] = useState('');
  const [endDate, setEndDate] = useState('');
  const [targetLift, setTargetLift] = useState(10);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [evaluation, setEvaluation] = useState<PromoEvaluation | null>(null);
  const [campaigns, setCampaigns] = useState<PromoCampaign[]>([]);
  const [, setSubmitting] = useState(false);

  // Fetch existing campaigns on mount
  useEffect(() => {
    fetchExistingCampaigns().then(setCampaigns).catch(() => {
      // Silently fail — calendar will be empty
    });
  }, []);

  const isFormValid = brand && region && promoType && budget && startDate && endDate && Number(budget) > 0;

  // The Evaluate button used to sit disabled with no explanation, which read as a
  // broken feature when it was really an unfilled field. Name what is missing.
  const missingFields = [
    !brand && 'brand',
    !region && 'region',
    !promoType && 'promotion type',
    (!budget || Number(budget) <= 0) && 'a budget above zero',
    !startDate && 'start date',
    !endDate && 'end date',
  ].filter((f): f is string => typeof f === 'string');

  const handleEvaluate = useCallback(async () => {
    if (!isFormValid || !promoType) return;
    setLoading(true);
    setError(null);
    setEvaluation(null);

    const formData: PromoFormData = {
      brand,
      region,
      promoType: promoType as PromoType,
      budget: Number(budget),
      startDate,
      endDate,
      targetLiftPercent: targetLift,
    };

    try {
      const result = await evaluatePromo(formData);
      setEvaluation(result);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Evaluation failed');
    } finally {
      setLoading(false);
    }
  }, [brand, region, promoType, budget, startDate, endDate, targetLift, isFormValid]);

  const handleSubmitApproval = useCallback(async () => {
    if (!evaluation || !promoType) return;
    setSubmitting(true);
    try {
      await submitForApproval(
        { brand, region, promoType: promoType as PromoType, budget: Number(budget), startDate, endDate, targetLiftPercent: targetLift },
        evaluation,
      );
    } catch {
      // Error handling would surface via approval notification
    } finally {
      setSubmitting(false);
    }
  }, [evaluation, brand, region, promoType, budget, startDate, endDate, targetLift]);

  const proposedCampaign = (brand && region && promoType && startDate && endDate) ? {
    name: `${promoType} — ${brand}`,
    brand,
    region,
    promoType: promoType as PromoType,
    budget: Number(budget) || 0,
    startDate,
    endDate,
    roi: evaluation?.roi,
  } : undefined;

  return (
    <div className={styles.container} data-testid="promo-task-module">
      <div className={styles.titleSection}>
        <span className={styles.title}>🎯 Campaign Planner</span>
        <span className={styles.subtitle}>Evaluate and plan promotional campaigns with AI-powered insights</span>
      </div>

      {/* Promo Type Selector */}
      <div>
        <div className={styles.sectionTitle}>Promotion Type</div>
        <PromoTypeSelector
          value={promoType}
          onChange={setPromoType}
          historicalRoi={{ Discount: 2.4, BOGO: 3.1, Display: 1.8, Digital: 2.9, Bundle: 2.2 }}
        />
      </div>

      {/* Campaign Form */}
      <Card className={styles.formCard} appearance="subtle">
        <div className={styles.formGrid}>
          <div className={styles.field}>
            <Label className={styles.fieldLabel}>Brand</Label>
            <Dropdown
              value={brand || ''}
              placeholder="Select brand..."
              selectedOptions={brand ? [brand] : []}
              onOptionSelect={(_, data) => setBrand(data.optionValue ?? '')}
              data-testid="brand-select"
              size="medium"
            >
              {TENANT_BRANDS.map(b => (
                <Option key={b} value={b}>{b}</Option>
              ))}
            </Dropdown>
          </div>

          <div className={styles.field}>
            <Label className={styles.fieldLabel}>Region</Label>
            <Dropdown
              value={region || ''}
              placeholder="Select region..."
              selectedOptions={region ? [region] : []}
              onOptionSelect={(_, data) => setRegion(data.optionValue ?? '')}
              data-testid="region-select"
              size="medium"
            >
              {TENANT_REGIONS.map(r => (
                <Option key={r} value={r}>{r}</Option>
              ))}
            </Dropdown>
          </div>

          <div className={styles.field}>
            <Label className={styles.fieldLabel}>Budget ($)</Label>
            <Input
              type="number"
              value={budget}
              onChange={(_e, data) => setBudget(data.value)}
              placeholder="25,000"
              contentBefore={<span style={{ color: '#94a3b8' }}>$</span>}
              data-testid="budget-input"
            />
          </div>

          <div className={styles.field}>
            <Label className={styles.fieldLabel}>Target Lift % (Optional)</Label>
            <div className={styles.sliderRow}>
              <Slider
                min={0}
                max={50}
                value={targetLift}
                onChange={(_e, data) => setTargetLift(data.value)}
                style={{ flex: 1 }}
                data-testid="target-lift-slider"
              />
              <span className={styles.sliderValue}>{targetLift}%</span>
            </div>
          </div>

          <div className={styles.field}>
            <Label className={styles.fieldLabel}>Start Date</Label>
            <Input
              type="date"
              value={startDate}
              onChange={(_e, data) => setStartDate(data.value)}
              data-testid="start-date-input"
            />
          </div>

          <div className={styles.field}>
            <Label className={styles.fieldLabel}>End Date</Label>
            <Input
              type="date"
              value={endDate}
              onChange={(_e, data) => setEndDate(data.value)}
              data-testid="end-date-input"
            />
          </div>
        </div>

        <div className={styles.actions}>
          {missingFields.length > 0 && (
            <span className={styles.missingHint} data-testid="evaluate-blocked-reason">
              Complete to evaluate: {missingFields.join(', ')}
            </span>
          )}
          <Button
            appearance="primary"
            size="large"
            className={styles.evaluateButton}
            disabled={!isFormValid || loading}
            onClick={handleEvaluate}
            data-testid="evaluate-button"
          >
            {loading ? <Spinner size="tiny" /> : '🎯 Evaluate Campaign'}
          </Button>
        </div>
      </Card>

      {/* Loading State */}
      {loading && (
        <div className={styles.loadingOverlay} data-testid="evaluation-loading">
          <Spinner size="medium" />
          <span className={styles.loadingText}>Analyzing campaign parameters against historical data...</span>
        </div>
      )}

      {/* Error */}
      {error && (
        <div className={styles.errorBanner} data-testid="evaluation-error">
          ❌ {error}
        </div>
      )}

      {/* Results */}
      {evaluation && (
        <>
          <PromoRecommendation
            evaluation={evaluation}
            budget={Number(budget)}
            onSubmitForApproval={handleSubmitApproval}
          />

          <ROIChart
            proposedRoi={evaluation.roi}
            proposedRoiLower={evaluation.roiLower}
            proposedRoiUpper={evaluation.roiUpper}
            historicalAvgRoi={evaluation.historicalAvgRoi}
            promoType={promoType as string}
          />
        </>
      )}

      {/* Calendar */}
      <PromoCalendar
        campaigns={campaigns}
        proposedCampaign={proposedCampaign}
      />
    </div>
  );
}
