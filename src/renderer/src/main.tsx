import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './theme/tokens.css'
import './base.css'
import { App } from './App'
import { syncTitleBarColors } from './chrome/titlebar'
import { Overlay } from './overlay/Overlay'

const root = document.getElementById('root')
if (!root) throw new Error('index.html has no #root element')

// One bundle serves two views: main loads it with #chrome for the window and #overlay for the top layer.
const isOverlay = location.hash === '#overlay'
document.documentElement.dataset['view'] = isOverlay ? 'overlay' : 'chrome'
if (!isOverlay) syncTitleBarColors()

createRoot(root).render(<StrictMode>{isOverlay ? <Overlay /> : <App />}</StrictMode>)
