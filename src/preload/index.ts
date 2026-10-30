import { contextBridge, ipcRenderer, type IpcRendererEvent } from 'electron'
import type { EventChannel, EventContract, InvokeChannel, InvokeContract, Unsubscribe, ZingApi } from '@shared/ipc'

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

const api: ZingApi = {
	runCommand: (id) => invoke('command:run', id),
	getTabs: () => invoke('tabs:get'),
	activateTab: (id) => invoke('tabs:activate', id),
	closeTab: (id) => invoke('tabs:close', id),
	navigateTab: (id, url) => invoke('tabs:navigate', id, url),
	moveTab: (id, index) => invoke('tabs:move', id, index),
	pinTab: (id) => invoke('tabs:pin', id),
	unpinTab: (id) => invoke('tabs:unpin', id),
	setTitleBarColors: (colors) => invoke('chrome:titlebar-colors', colors),
	openAppMenu: (anchor) => invoke('chrome:app-menu', anchor),
	openTabMenu: (id, anchor) => invoke('chrome:tab-menu', id, anchor),
	onTabsState: (listener) => listen('tabs:state', listener),
	onFocusAddress: (listener) => listen('chrome:focus-address', () => listener()),
	onNotice: (listener) => listen('overlay:notice', listener),
}

contextBridge.exposeInMainWorld('zing', api)
