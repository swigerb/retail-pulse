import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { Dashboard } from './components/Dashboard';
import './App.css';

function App() {
  return (
    <FluentProvider theme={teamsDarkTheme}>
      <Dashboard />
    </FluentProvider>
  );
}

export default App;
