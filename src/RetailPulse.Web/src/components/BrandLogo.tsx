interface Props {
  size?: number;
  className?: string;
  showWordmark?: boolean;
}

export function BrandLogo({ size = 40, className, showWordmark = true }: Props) {
  return (
    <div className={`brand-logo ${className || ''}`} style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
      <div style={{
        height: size,
        width: size,
        background: 'linear-gradient(135deg, var(--brand-primary), var(--brand-accent))',
        borderRadius: '8px',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        fontSize: size * 0.5,
        fontWeight: 700,
        color: '#fff',
        fontFamily: 'Inter, system-ui, sans-serif',
      }}>
        RP
      </div>
      {showWordmark && (
        <div className="brand-wordmark">
          <span className="brand-name">RETAIL PULSE</span>
        </div>
      )}
    </div>
  );
}
