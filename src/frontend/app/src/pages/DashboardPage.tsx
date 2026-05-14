import { useEffect, useState } from 'react'
import { api } from '../services/api'

interface HealthStatus {
  status: string
  database: boolean
}

export default function DashboardPage() {
  const [health, setHealth] = useState<HealthStatus | null>(null)

  useEffect(() => {
    api.get<HealthStatus>('/health').then(setHealth).catch(() => setHealth(null))
  }, [])

  return (
    <div>
      <h2>Dashboard</h2>
      <p>Welcome to PirateChess! Course export functionality coming in Phase 2.</p>
      {health && (
        <p>
          API Status: {health.status} | Database: {health.database ? 'Connected' : 'Disconnected'}
        </p>
      )}
    </div>
  )
}
