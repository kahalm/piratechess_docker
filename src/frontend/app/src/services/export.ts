import { api } from './api'

export interface ExportStatus {
  id: number
  status: string
  chessableBid: string
  courseName: string
  trainingMode: string
  chapterCount: number
  lineCount: number
  startedAt: string
  completedAt: string | null
}

export const exportService = {
  startExport: (bid: string, courseName: string, trainingMode: string) =>
    api.post<ExportStatus>('/export', { bid, courseName, trainingMode }),

  getExports: () =>
    api.get<ExportStatus[]>('/export'),

  getExport: (id: number) =>
    api.get<ExportStatus>(`/export/${id}`),

  downloadPgn: async (id: number, fileName: string) => {
    const blob = await api.getBlob(`/export/${id}/pgn`)
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = fileName
    document.body.appendChild(a)
    a.click()
    document.body.removeChild(a)
    URL.revokeObjectURL(url)
  },
}
