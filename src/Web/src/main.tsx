import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
import './styles.css';
import { installNetworkWatcher } from './utils/networkWatcher';
import { logger } from './utils/logger';

// Install interceptors as early as possible so the Network tab captures
// everything — including MSAL's own traffic.
installNetworkWatcher();
logger.info('boot', 'CMSP demo app starting');

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
