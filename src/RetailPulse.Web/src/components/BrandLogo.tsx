interface Props {
  size?: number;
  className?: string;
  showWordmark?: boolean;
}

export function BrandLogo({ size = 40, className, showWordmark = true }: Props) {
  return (
    <div
      className={`brand-logo ${className || ''}`.trim()}
      data-show-wordmark={showWordmark}
      style={{ display: 'inline-flex', alignItems: 'center' }}
    >
      <img
        src="/retail-pulse-logo.jpg"
        alt="Retail Pulse"
        style={{ height: size, width: 'auto', display: 'block' }}
      />
    </div>
  );
}
