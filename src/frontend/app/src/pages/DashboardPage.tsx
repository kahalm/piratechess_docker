import { useEffect, useState, useCallback } from 'react'
import { api } from '../services/api'
import { chessableService, CourseListItem } from '../services/chessable'
import { exportService, ExportStatus } from '../services/export'
import {
  useExportProgress,
  ExportProgress,
  ExportCompleted,
  ExportFailed,
} from '../hooks/useExportProgress'

interface HealthStatus {
  status: string
  database: boolean
}

type TrainingMode = 'AllKeyMoves' | 'FirstKeyMove' | 'None'

interface ActiveProgress {
  [exportId: number]: ExportProgress
}

export default function DashboardPage() {
  const [health, setHealth] = useState<HealthStatus | null>(null)
  const [courses, setCourses] = useState<CourseListItem[]>([])
  const [loadingCourses, setLoadingCourses] = useState(false)
  const [courseError, setCourseError] = useState('')
  const [selectedModes, setSelectedModes] = useState<Record<string, TrainingMode>>({})
  const [startingExport, setStartingExport] = useState<string | null>(null)
  const [activeProgress, setActiveProgress] = useState<ActiveProgress>({})
  const [recentExports, setRecentExports] = useState<ExportStatus[]>([])

  useEffect(() => {
    api.get<HealthStatus>('/health').then(setHealth).catch(() => setHealth(null))
    exportService.getExports().then(setRecentExports).catch(() => {})
  }, [])

  const onProgress = useCallback((msg: ExportProgress) => {
    setActiveProgress(prev => ({ ...prev, [msg.exportId]: msg }))
  }, [])

  const onCompleted = useCallback((msg: ExportCompleted) => {
    setActiveProgress(prev => {
      const next = { ...prev }
      delete next[msg.exportId]
      return next
    })
    exportService.getExports().then(setRecentExports).catch(() => {})
  }, [])

  const onFailed = useCallback((msg: ExportFailed) => {
    setActiveProgress(prev => {
      const next = { ...prev }
      delete next[msg.exportId]
      return next
    })
    alert(`Export failed: ${msg.error}`)
    exportService.getExports().then(setRecentExports).catch(() => {})
  }, [])

  useExportProgress(onProgress, onCompleted, onFailed)

  const loadCourses = async () => {
    setLoadingCourses(true)
    setCourseError('')
    try {
      const list = await chessableService.getCourses()
      setCourses(list)
    } catch (err) {
      setCourseError(err instanceof Error ? err.message : 'Failed to load courses')
    } finally {
      setLoadingCourses(false)
    }
  }

  const startExport = async (course: CourseListItem) => {
    const mode = selectedModes[course.bid] || 'FirstKeyMove'
    setStartingExport(course.bid)
    try {
      const exp = await exportService.startExport(course.bid, course.name, mode)
      setActiveProgress(prev => ({
        ...prev,
        [exp.id]: {
          exportId: exp.id,
          phase: 'Starting',
          detail: 'Export queued...',
          chaptersDone: 0,
          chaptersTotal: 0,
          linesDone: 0,
        },
      }))
      setRecentExports(prev => [exp, ...prev])
    } catch (err) {
      alert(err instanceof Error ? err.message : 'Export start failed')
    } finally {
      setStartingExport(null)
    }
  }

  const handleDownload = async (exp: ExportStatus) => {
    const fileName = `${exp.courseName.replace(/\s+/g, '_')}_${exp.trainingMode}.pgn`
    await exportService.downloadPgn(exp.id, fileName)
  }

  const runningExports = recentExports.filter(e => e.status === 'Running')
  const completedExports = recentExports.filter(e => e.status === 'Completed').slice(0, 5)

  return (
    <div>
      <h2>Dashboard</h2>

      {health && (
        <p style={{ fontSize: '0.85rem', color: '#666' }}>
          API: {health.status} | DB: {health.database ? 'Connected' : 'Disconnected'}
        </p>
      )}

      {/* Course List */}
      <section style={{ marginTop: '1.5rem' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: '1rem' }}>
          <h3 style={{ margin: 0 }}>Chessable Courses</h3>
          <button onClick={loadCourses} disabled={loadingCourses}>
            {loadingCourses ? 'Loading...' : 'Load Courses'}
          </button>
        </div>

        {courseError && <p style={{ color: 'red' }}>{courseError}</p>}

        {courses.length > 0 && (
          <table style={{ width: '100%', borderCollapse: 'collapse', marginTop: '0.5rem' }}>
            <thead>
              <tr style={{ borderBottom: '2px solid #ccc', textAlign: 'left' }}>
                <th style={{ padding: '0.5rem' }}>BID</th>
                <th style={{ padding: '0.5rem' }}>Course Name</th>
                <th style={{ padding: '0.5rem' }}>Training Mode</th>
                <th style={{ padding: '0.5rem' }}>Action</th>
              </tr>
            </thead>
            <tbody>
              {courses.map(c => (
                <tr key={c.bid} style={{ borderBottom: '1px solid #eee' }}>
                  <td style={{ padding: '0.5rem', fontFamily: 'monospace' }}>{c.bid}</td>
                  <td style={{ padding: '0.5rem' }}>{c.name}</td>
                  <td style={{ padding: '0.5rem' }}>
                    <select
                      value={selectedModes[c.bid] || 'FirstKeyMove'}
                      onChange={e =>
                        setSelectedModes(prev => ({
                          ...prev,
                          [c.bid]: e.target.value as TrainingMode,
                        }))
                      }
                    >
                      <option value="FirstKeyMove">First Key Move</option>
                      <option value="AllKeyMoves">All Key Moves</option>
                      <option value="None">No Training</option>
                    </select>
                  </td>
                  <td style={{ padding: '0.5rem' }}>
                    <button
                      onClick={() => startExport(c)}
                      disabled={startingExport === c.bid}
                    >
                      {startingExport === c.bid ? 'Starting...' : 'Export'}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      {/* Active Exports */}
      {(Object.keys(activeProgress).length > 0 || runningExports.length > 0) && (
        <section style={{ marginTop: '1.5rem' }}>
          <h3>Active Exports</h3>
          {Object.values(activeProgress).map(p => (
            <div
              key={p.exportId}
              style={{
                padding: '0.75rem',
                border: '1px solid #ccc',
                borderRadius: 4,
                marginBottom: '0.5rem',
              }}
            >
              <strong>Export #{p.exportId}</strong> — {p.phase}
              <div style={{ fontSize: '0.85rem', color: '#555', marginTop: '0.25rem' }}>
                {p.detail}
              </div>
              {p.chaptersTotal > 0 && (
                <div style={{ marginTop: '0.25rem' }}>
                  Chapters: {p.chaptersDone}/{p.chaptersTotal} | Lines: {p.linesDone}
                  <div
                    style={{
                      height: 6,
                      background: '#eee',
                      borderRadius: 3,
                      marginTop: 4,
                    }}
                  >
                    <div
                      style={{
                        height: '100%',
                        background: '#4caf50',
                        borderRadius: 3,
                        width: `${(p.chaptersDone / p.chaptersTotal) * 100}%`,
                        transition: 'width 0.3s',
                      }}
                    />
                  </div>
                </div>
              )}
            </div>
          ))}
        </section>
      )}

      {/* Recent Completed */}
      {completedExports.length > 0 && (
        <section style={{ marginTop: '1.5rem' }}>
          <h3>Recent Exports</h3>
          <table style={{ width: '100%', borderCollapse: 'collapse' }}>
            <thead>
              <tr style={{ borderBottom: '2px solid #ccc', textAlign: 'left' }}>
                <th style={{ padding: '0.5rem' }}>Course</th>
                <th style={{ padding: '0.5rem' }}>Mode</th>
                <th style={{ padding: '0.5rem' }}>Chapters</th>
                <th style={{ padding: '0.5rem' }}>Lines</th>
                <th style={{ padding: '0.5rem' }}>Action</th>
              </tr>
            </thead>
            <tbody>
              {completedExports.map(e => (
                <tr key={e.id} style={{ borderBottom: '1px solid #eee' }}>
                  <td style={{ padding: '0.5rem' }}>{e.courseName}</td>
                  <td style={{ padding: '0.5rem' }}>{e.trainingMode}</td>
                  <td style={{ padding: '0.5rem' }}>{e.chapterCount}</td>
                  <td style={{ padding: '0.5rem' }}>{e.lineCount}</td>
                  <td style={{ padding: '0.5rem' }}>
                    <button onClick={() => handleDownload(e)}>Download PGN</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}
    </div>
  )
}
