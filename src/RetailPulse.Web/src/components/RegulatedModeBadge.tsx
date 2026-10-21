import { useEffect, useState } from 'react';
import { resolveApiUrl } from '../config/apiOrigin';

export interface GatewayProfile {
  enabled: boolean;
  mode: string;
  label: string;
  disclaimer: string;
}

/** Shows the gateway profile; regulated-retail mode is a config posture, never a HIPAA claim. */
export function RegulatedModeBadge({ profile }: { profile?: GatewayProfile | null }) {
  const [fetched, setFetched] = useState<GatewayProfile | null>(null);

  useEffect(() => {
    if (profile !== undefined) return;
    let cancelled = false;
    (async () => {
      try {
        const res = await fetch(resolveApiUrl('/api/gateway/profile'));
        if (res?.ok) {
          const body = (await res.json()) as GatewayProfile;
          if (!cancelled) setFetched(body);
        }
      } catch {
        // Indicator is advisory; absence must not break chat.
      }
    })();
    return () => { cancelled = true; };
  }, [profile]);

  const p = profile === undefined ? fetched : profile;
  if (!p?.enabled) return null;

  return (
    <div
      role="status"
      data-testid="regulated-mode-badge"
      data-gateway-profile={p.mode}
      title={p.disclaimer}
      style={{
        padding: '4px 10px',
        fontSize: '12px',
        fontWeight: 600,
        color: '#facc15',
        backgroundColor: 'rgba(250,204,21,0.10)',
        border: '1px solid rgba(250,204,21,0.35)',
        borderRadius: '6px',
      }}
    >
      {p.label}
    </div>
  );
}
