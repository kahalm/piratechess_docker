import { useEffect, useState } from 'react'
import { exportService, ExportStatus } from '../services/export'

export default function ExportHistoryPage() {
  const [exports, setExports] = useState<ExportStatus[]>([])
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    exportService
      .getExports()
      .then(setExports)
      .catch(() => {})
      .finally(() => setLoading(false))
  }, [])

  const handleDownload = async (exp: ExportStatus) => {
    const fileName = `${exp.courseName.replace(/\s+/g, '_')}_${exp.trainingMode}.pgn`
    await exportService.downloadPgn(exp.id, fileName)
  }

  const statusColor = (status: string) => {
    switch (status) {
      case 'Completed':
        return 'green'
      case 'Running':
        return 'orange'
      case 'Failed':
        return 'red'
      default:
        return '#888'
    }
  }

  if (loading) return <p>Loading...</p>

  return (
    <div>
      <h2>Export History</h2>

      {exports.length === 0 ? (
        <p>No exports yet.</p>
      ) : (
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr style={{ borderBottom: '2px solid #ccc', textAlign: 'left' }}>
              <th style={{ padding: '0.5rem' }}>Course</th>
              <th style={{ padding: '0.5rem' }}>Mode</th>
              <th style={{ padding: '0.5rem' }}>Status</th>
              <th style={{ padding: '0.5rem' }}>Chapters</th>
              <th style={{ padding: '0.5rem' }}>Lines</th>
              <th style={{ padding: '0.5rem' }}>Started</th>
              <th style={{ padding: '0.5rem' }}>Action</th>
            </tr>
          </thead>
          <tbody>
            {exports.map(e => (
              <tr key={e.id} style={{ borderBottom: '1px solid #eee' }}>
                <td style={{ padding: '0.5rem' }}>{e.courseName}</td>
                <td style={{ padding: '0.5rem' }}>{e.trainingMode}</td>
                <td style={{ padding: '0.5rem', color: statusColor(e.status) }}>
                  {e.status}
                </td>
                <td style={{ padding: '0.5rem' }}>{e.chapterCount}</td>
                <td style={{ padding: '0.5rem' }}>{e.lineCount}</td>
                <td style={{ padding: '0.5rem' }}>
                  {new Date(e.startedAt).toLocaleString()}
                </td>
                <td style={{ padding: '0.5rem' }}>
                  {e.status === 'Completed' && (
                    <button onClick={() => handleDownload(e)}>Download PGN</button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
