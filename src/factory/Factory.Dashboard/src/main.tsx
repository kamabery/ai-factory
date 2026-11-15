import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import { App } from './App'
import { Providers } from './app/Providers'
import { newQueryClient } from './app/query'
import { applySavedTheme } from './lib/theme'

applySavedTheme()
createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Providers client={newQueryClient()}>
      <App />
    </Providers>
  </StrictMode>,
)
