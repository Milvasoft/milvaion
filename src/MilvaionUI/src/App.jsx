import { BrowserRouter as Router, Routes, Route, Navigate } from 'react-router-dom'
import { ThemeProvider } from './contexts/ThemeContext'
import Layout from './components/Layout'
import ProtectedRoute from './components/ProtectedRoute'
import Login from './pages/Login/Login'
import Dashboard from './pages/Dashboard'
import JobList from './pages/Jobs/JobList'
import JobDetail from './pages/Jobs/JobDetail'
import JobForm from './pages/Jobs/JobForm'
import OccurrenceDetail from './pages/Occurrences/OccurrenceDetail'
import WorkerList from './pages/Workers/WorkerList'
import ExecutionList from './pages/Executions/ExecutionList'
import Tags from './pages/Tags'
import AdminDashboard from './pages/Admin/AdminDashboard'
import Configuration from './pages/Configuration'
import FailedOccurrenceList from './pages/FailedOccurrences/FailedOccurrenceList'
import FailedOccurrenceDetail from './pages/FailedOccurrences/FailedOccurrenceDetail'

function App() {
  return (
    <ThemeProvider>
      <Router>
        <Routes>
          {/* Public route - Login */}
          <Route path="/login" element={<Login />} />

          {/* Protected routes - Wrapped in Layout */}
          <Route
            path="/*"
            element={
              <ProtectedRoute>
                <Layout>
                  <Routes>
                    <Route path="/" element={<Navigate to="/dashboard" replace />} />
                    <Route path="/dashboard" element={<Dashboard />} />
                    <Route path="/jobs" element={<JobList />} />
                    <Route path="/jobs/new" element={<JobForm />} />
                    <Route path="/jobs/:id" element={<JobDetail />} />
                    <Route path="/jobs/:id/edit" element={<JobForm />} />
                    <Route path="/occurrences/:id" element={<OccurrenceDetail />} />
                    <Route path="/executions" element={<ExecutionList />} />
                    <Route path="/workers" element={<WorkerList />} />
                    <Route path="/tags" element={<Tags />} />
                    <Route path="/failed-executions" element={<FailedOccurrenceList />} />
                    <Route path="/failed-executions/:id" element={<FailedOccurrenceDetail />} />
                    <Route path="/admin" element={<AdminDashboard />} />
                    <Route path="/configuration" element={<Configuration />} />

                    {/* Catch all - redirect to dashboard */}
                    <Route path="*" element={<Navigate to="/dashboard" replace />} />
                  </Routes>
                </Layout>
              </ProtectedRoute>
            }
          />
        </Routes>
      </Router>
    </ThemeProvider>
  )
}

export default App
