interface Props {
  size?: number;
  className?: string;
  showWordmark?: boolean;
}

export function PatronLogo({ size = 40, className, showWordmark = true }: Props) {
  return (
    <div className={`patron-logo ${className || ''}`} style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
      <img
        src="/patron-logo.png"
        alt="Patrón Tequila"
        style={{ height: size, width: 'auto', filter: 'invert(1)' }}
      />
      {showWordmark && (
        <div className="patron-wordmark">
          <span className="retail-pulse-name">PULSE</span>
        </div>
      )}
    </div>
  );
}
