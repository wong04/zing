import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
// First, so the view's error reporter listens before anything else runs.
import { errorReporter } from './api'
import './theme/tokens.css'
import './base.css'
import { App } from './App'
import { Overlay } from './overlay/Overlay'
import { PageUi } from './overlay/PageUi'
import { Surface } from './surface/Surface'

const root = document.getElementById('root')
if (!root) throw new Error('index.html has no #root element')

// One bundle serves two views: main loads it with #chrome for the window and #overlay for the top layer.
const isPageUi = location.hash === '#pageui'
const isSurface = location.hash === '#surface'
const isOverlay = location.hash.startsWith('#overlay') || isPageUi
document.documentElement.dataset['view'] = isSurface ? 'surface' : isOverlay ? 'overlay' : 'chrome'

// An error a boundary catches (a tool panel that failed to load or render) still shows the user a
// fallback, but it is Zing's bug all the same: report it, so an e2e run fails on it.
const onCaughtError = (error: unknown): void => {
	console.error(error)
	errorReporter.error(error)
}

createRoot(root, { onCaughtError }).render(<StrictMode>{isSurface ? <Surface /> : isPageUi ? <PageUi /> : isOverlay ? <Overlay /> : <App />}</StrictMode>)
