import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, act } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { StreamingMessage } from '../components/streaming/StreamingMessage';

function renderWithTheme(ui: React.ReactElement) {
  return render(<FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>);
}

describe('StreamingMessage', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  it('shows "Generating..." when streaming with no tokens', () => {
    renderWithTheme(<StreamingMessage tokens="" isStreaming={true} />);
    expect(screen.getByTestId('streaming-generating')).toBeInTheDocument();
    expect(screen.getByText('Generating...')).toBeInTheDocument();
  });

  it('renders streaming cursor when tokens are present and streaming', () => {
    renderWithTheme(<StreamingMessage tokens="Hello world" isStreaming={true} />);
    // Wait for tokens to display
    act(() => { vi.advanceTimersByTime(500); });
    expect(screen.getByTestId('streaming-message')).toBeInTheDocument();
    expect(screen.getByTestId('streaming-cursor')).toBeInTheDocument();
  });

  it('hides cursor when streaming completes', () => {
    const { rerender } = renderWithTheme(
      <StreamingMessage tokens="Done" isStreaming={true} />,
    );
    act(() => { vi.advanceTimersByTime(500); });

    rerender(
      <FluentProvider theme={teamsDarkTheme}>
        <StreamingMessage tokens="Done" isStreaming={false} />
      </FluentProvider>,
    );
    act(() => { vi.advanceTimersByTime(500); });
    expect(screen.queryByTestId('streaming-cursor')).not.toBeInTheDocument();
  });

  it('calls onComplete when streaming ends', () => {
    const onComplete = vi.fn();
    const { rerender } = renderWithTheme(
      <StreamingMessage tokens="Text" isStreaming={true} onComplete={onComplete} />,
    );
    act(() => { vi.advanceTimersByTime(500); });

    rerender(
      <FluentProvider theme={teamsDarkTheme}>
        <StreamingMessage tokens="Text" isStreaming={false} onComplete={onComplete} />
      </FluentProvider>,
    );
    act(() => { vi.advanceTimersByTime(500); });
    expect(onComplete).toHaveBeenCalledTimes(1);
  });

  it('progressively reveals tokens', () => {
    renderWithTheme(<StreamingMessage tokens="Hello" isStreaming={true} />);
    // Initially, displayed length is 0 — shows generating
    expect(screen.queryByTestId('streaming-generating')).not.toBeInTheDocument(); // tokens are present
    // After some time, tokens appear
    act(() => { vi.advanceTimersByTime(200); });
    expect(screen.getByTestId('streaming-message')).toBeInTheDocument();
  });

  it('renders markdown content', () => {
    renderWithTheme(<StreamingMessage tokens="**bold text**" isStreaming={false} />);
    act(() => { vi.advanceTimersByTime(500); });
    const container = screen.getByTestId('streaming-message');
    expect(container.querySelector('strong')).toBeInTheDocument();
  });

  afterEach(() => {
    vi.useRealTimers();
  });
});
