import { useEffect, useRef, useState, useCallback } from 'react'
import { HubConnectionBuilder, HubConnection, LogLevel } from '@microsoft/signalr'

export interface ExportProgress {
  exportId: number
  phase: string
  detail: string
  chaptersDone: number
  chaptersTotal: number
  linesDone: number
}

export interface ExportCompleted {
  exportId: number
  courseName: string
  chapterCount: number
  lineCount: number
  pgnSize: number
}

export interface ExportFailed {
  exportId: number
  error: string
}

export function useExportProgress(
  onProgress?: (msg: ExportProgress) => void,
  onCompleted?: (msg: ExportCompleted) => void,
  onFailed?: (msg: ExportFailed) => void,
) {
  const connectionRef = useRef<HubConnection | null>(null)
  const [connected, setConnected] = useState(false)

  // Keep latest callbacks in refs to avoid reconnecting on callback change
  const onProgressRef = useRef(onProgress)
  const onCompletedRef = useRef(onCompleted)
  const onFailedRef = useRef(onFailed)
  onProgressRef.current = onProgress
  onCompletedRef.current = onCompleted
  onFailedRef.current = onFailed

  const connect = useCallback(() => {
    const token = localStorage.getItem('token')
    if (!token) return

    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/export-progress', {
        accessTokenFactory: () => token,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('ExportProgress', (msg: ExportProgress) => {
      onProgressRef.current?.(msg)
    })

    connection.on('ExportCompleted', (msg: ExportCompleted) => {
      onCompletedRef.current?.(msg)
    })

    connection.on('ExportFailed', (msg: ExportFailed) => {
      onFailedRef.current?.(msg)
    })

    connection.onclose(() => setConnected(false))
    connection.onreconnected(() => setConnected(true))

    connection
      .start()
      .then(() => setConnected(true))
      .catch(err => console.error('SignalR connect failed:', err))

    connectionRef.current = connection
  }, [])

  useEffect(() => {
    connect()
    return () => {
      connectionRef.current?.stop()
    }
  }, [connect])

  return { connected }
}
