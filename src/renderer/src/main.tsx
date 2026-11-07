import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
// First, so the view's error reporter listens before anything else runs.
import { errorReporter } from './api'
import './theme/tokens.css'
import './base.css'
import { App } from './App'
import { AccountPicker } from './overlay/AccountPicker'
import { Overlay } from './overlay/Overlay'
import { PageUi } from './overlay/PageUi'
import { Surface } from './surface/Surface'

const root = document.getElementById('root')
if (!root) throw new Error('index.html has no #root element')

// One bundle serves every view: the host loads it with #chrome for the window, #overlay for the top layer, #pageui and #picker over the page.
const isPageUi = location.hash === '#pageui'
const isPicker = location.hash === '#picker'
const isSurface = location.hash === '#surface'
const isOverlay = location.hash.startsWith('#overlay') || isPageUi || isPicker
document.documentElement.dataset['view'] = isSurface ? 'surface' : isOverlay ? 'overlay' : 'chrome'

// An error a boundary catches (a tool panel that failed to load or render) still shows the user a
// fallback, but it is Zing's bug all the same: report it, so an e2e run fails on it.
const onCaughtError = (error: unknown): void => {
	console.error(error)
	errorReporter.error(error)
}

createRoot(root, { onCaughtError }).render(<StrictMode>{isSurface ? <Surface /> : isPageUi ? <PageUi /> : isPicker ? <AccountPicker /> : isOverlay ? <Overlay /> : <App />}</StrictMode>)
