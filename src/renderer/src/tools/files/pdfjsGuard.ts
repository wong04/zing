// Imported first by the pdf.js worker (pdfjsWorker.ts): ES modules run in import order, so the worker's network is narrowed before
// pdf.js has run a line. Data and blob addresses stay open, since pdf.js works with pictures it builds.
import { restrictNetwork } from '@shared/convert/workerNetwork'
import { VIEW_ORIGIN } from './bgremoveProtocol'

restrictNetwork(self as unknown as Record<string, unknown>, self.location.origin, VIEW_ORIGIN, ['data:', 'blob:'])
