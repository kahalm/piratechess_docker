import { NavLink, Outlet } from 'react-router-dom'
import { useAuth } from '../context/AuthContext'

const linkStyle: React.CSSProperties = {
  marginRight: '1rem',
  textDecoration: 'none',
  color: '#555',
}

const activeLinkStyle: React.CSSProperties = {
  ...linkStyle,
  fontWeight: 'bold',
  color: '#000',
}

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
      <nav style={{ marginBottom: '1rem' }}>
        <NavLink to="/dashboard" style={({ isActive }) => isActive ? activeLinkStyle : linkStyle}>
          Dashboard
        </NavLink>
        <NavLink to="/credentials" style={({ isActive }) => isActive ? activeLinkStyle : linkStyle}>
          Credentials
        </NavLink>
        <NavLink to="/history" style={({ isActive }) => isActive ? activeLinkStyle : linkStyle}>
          Export History
        </NavLink>
      </nav>
      <main>
        <Outlet />
      </main>
    </div>
  )
}
