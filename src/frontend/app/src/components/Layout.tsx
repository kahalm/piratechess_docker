import { Outlet } from 'react-router-dom'
import { useAuth } from '../context/AuthContext'

export default function Layout() {
  const { username, logout } = useAuth()

  return (
    <div style={{ maxWidth: 960, margin: '0 auto', padding: '1rem' }}>
      <header style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', borderBottom: '1px solid #ccc', paddingBottom: '0.5rem', marginBottom: '1rem' }}>
        <h1 style={{ margin: 0 }}>PirateChess</h1>
        <div>
          <span style={{ marginRight: '1rem' }}>{username}</span>
          <button onClick={logout}>Logout</button>
        </div>
      </header>
      <main>
        <Outlet />
      </main>
    </div>
  )
}
