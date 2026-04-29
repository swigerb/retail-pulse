import { Component, type ErrorInfo, type ReactNode } from 'react';

interface Props {
  children: ReactNode;
  fallback?: ReactNode;
}

interface State {
  hasError: boolean;
  error?: Error;
}

export class ErrorBoundary extends Component<Props, State> {
  state: State = { hasError: false };

  static getDerivedStateFromError(error: Error): State {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    if (import.meta.env.DEV) {
      console.error('ErrorBoundary caught:', error, info);
    }
  }

  handleReset = () => {
    this.setState({ hasError: false, error: undefined });
  };

  render() {
    if (this.state.hasError) {
      if (this.props.fallback) return this.props.fallback;
      return (
        <div
          role="alert"
          style={{
            display: 'flex',
            flexDirection: 'column',
            alignItems: 'center',
            justifyContent: 'center',
            height: '100vh',
            padding: '24px',
            background: 'var(--color-bg, #080808)',
            color: 'var(--color-text, #F5F5F0)',
            textAlign: 'center',
            gap: '16px',
          }}
        >
          <h1 style={{ color: 'var(--brand-accent, #E8A838)', margin: 0 }}>
            Something went wrong
          </h1>
          <p style={{ color: 'var(--color-text-muted, #A0A0A0)', maxWidth: 480 }}>
            The Retail Pulse dashboard hit an unexpected error. Try reloading; if
            the problem persists, contact your administrator.
          </p>
          {import.meta.env.DEV && this.state.error && (
            <pre
              style={{
                background: 'var(--color-surface, #1A1A1A)',
                padding: '12px',
                borderRadius: 8,
                maxWidth: 720,
                overflow: 'auto',
                fontSize: 12,
                color: 'var(--color-text-muted, #A0A0A0)',
              }}
            >
              {this.state.error.message}
            </pre>
          )}
          <button
            onClick={this.handleReset}
            style={{
              padding: '8px 20px',
              borderRadius: 6,
              border: '1px solid var(--brand-accent, #E8A838)',
              background: 'transparent',
              color: 'var(--brand-accent, #E8A838)',
              cursor: 'pointer',
              fontSize: 14,
            }}
          >
            Try again
          </button>
        </div>
      );
    }
    return this.props.children;
  }
}
