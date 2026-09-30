const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('classroom', {
  info: () => ipcRenderer.invoke('info'), connect: address => ipcRenderer.invoke('connect', address),
  api: (route, data) => ipcRenderer.invoke('api', route, data),
  clipboardAttempt: () => ipcRenderer.send('clipboard-attempt'),
  onPolicy: callback => ipcRenderer.on('policy', (_event, value) => callback(value))
});
