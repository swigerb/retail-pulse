import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { Dashboard } from './components/Dashboard';
import { ErrorBoundary } from './components/ErrorBoundary';
import './App.css';

function App() {
  return (
    <ErrorBoundary>
      <FluentProvider theme={teamsDarkTheme}>
        <Dashboard />
      </FluentProvider>
    </ErrorBoundary>
  );
}

export default App;
