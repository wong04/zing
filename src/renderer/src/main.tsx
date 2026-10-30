import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './theme/tokens.css'
import './base.css'
import { App } from './App'
import { syncTitleBarColors } from './chrome/titlebar'
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
if (!isOverlay && !isSurface) syncTitleBarColors()

createRoot(root).render(<StrictMode>{isSurface ? <Surface /> : isPageUi ? <PageUi /> : isOverlay ? <Overlay /> : <App />}</StrictMode>)
