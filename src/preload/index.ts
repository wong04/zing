import { contextBridge, ipcRenderer, type IpcRendererEvent } from 'electron'
import type { EventChannel, EventContract, InvokeChannel, InvokeContract, Unsubscribe } from '@shared/ipc'
import { createZingApi } from '@shared/zingApi'

function invoke<C extends InvokeChannel>(
	channel: C,
	...args: InvokeContract[C]['args']
): Promise<InvokeContract[C]['result']> {
	return ipcRenderer.invoke(channel, ...args)
}

function listen<C extends EventChannel>(channel: C, listener: (payload: EventContract[C]) => void): Unsubscribe {
	// Never hand the IpcRendererEvent to the page: it carries `sender`, a way back into ipcRenderer.
	const wrapped = (_event: IpcRendererEvent, payload: EventContract[C]): void => listener(payload)
	ipcRenderer.on(channel, wrapped)
	return () => ipcRenderer.removeListener(channel, wrapped)
}

contextBridge.exposeInMainWorld('zing', createZingApi(invoke, listen))
