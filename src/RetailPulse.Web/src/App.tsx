import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { Dashboard } from './components/Dashboard';
import { ErrorBoundary } from './components/ErrorBoundary';
import { AuthGate } from './auth/AuthGate';
import './App.css';

function App() {
  return (
    <ErrorBoundary>
      <FluentProvider theme={teamsDarkTheme}>
        <AuthGate>
          <Dashboard />
        </AuthGate>
      </FluentProvider>
    </ErrorBoundary>
  );
}

export default App;
