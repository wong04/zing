// Imported first by both workers: ES modules run in import order, so this narrows the worker's network before onnxruntime (or anything
// else imported after it) has run a line.
import { restrictNetwork } from '@shared/convert/workerNetwork'
import { VIEW_ORIGIN } from './bgremoveProtocol'

restrictNetwork(self as unknown as Record<string, unknown>, self.location.origin, VIEW_ORIGIN)
