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
import UpcomingExecutions from './pages/UpcomingExecutions/UpcomingExecutions'
import Tags from './pages/Tags'
import AdminDashboard from './pages/Admin/AdminDashboard'
import Configuration from './pages/Configuration'
import FailedOccurrenceList from './pages/FailedOccurrences/FailedOccurrenceList'
import FailedOccurrenceDetail from './pages/FailedOccurrences/FailedOccurrenceDetail'
import UserList from './pages/UserManagement/UserList'
import RoleList from './pages/UserManagement/RoleList'
import ApiKeyList from './pages/UserManagement/ApiKeyList'
import ActivityLogList from './pages/UserManagement/ActivityLogList'
import Profile from './pages/Profile/Profile'
import WorkflowList from './pages/Workflows/WorkflowList'
import WorkflowDetail from './pages/Workflows/WorkflowDetail'
import WorkflowForm from './pages/Workflows/WorkflowForm'
import WorkflowRunDetail from './pages/Workflows/WorkflowRunDetail'
import WorkflowBuilder from './pages/Workflows/WorkflowBuilder/WorkflowBuilder'
import ReportDashboard from './pages/Reports/ReportDashboard'
import FailureRateTrendReport from './pages/Reports/FailureRateTrendReport'
import PercentileDurationsReport from './pages/Reports/PercentileDurationsReport'
import TopSlowJobsReport from './pages/Reports/TopSlowJobsReport'
import WorkerThroughputReport from './pages/Reports/WorkerThroughputReport'
import WorkerUtilizationTrendReport from './pages/Reports/WorkerUtilizationTrendReport'
import CronScheduleVsActualReport from './pages/Reports/CronScheduleVsActualReport'
import JobHealthScoreReport from './pages/Reports/JobHealthScoreReport'
import WorkflowSuccessRateReport from './pages/Reports/WorkflowSuccessRateReport'
import WorkflowStepBottleneckReport from './pages/Reports/WorkflowStepBottleneckReport'
import WorkflowDurationTrendReport from './pages/Reports/WorkflowDurationTrendReport'

function App() {
  const basename = (window.__MILVAION_CONFIG__?.basePath ?? import.meta.env.VITE_BASE_PATH ?? '/') || '/'
  return (
    <ThemeProvider>
      <Router basename={basename}>
        <Routes>
          <Route path="/login" element={<Login />} />

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
                    <Route path="/upcoming" element={<UpcomingExecutions />} />
                    <Route path="/workers" element={<WorkerList />} />
                    <Route path="/tags" element={<Tags />} />
                    <Route path="/failed-executions" element={<FailedOccurrenceList />} />
                    <Route path="/failed-executions/:id" element={<FailedOccurrenceDetail />} />
                    <Route path="/admin" element={<AdminDashboard />} />
                    <Route path="/configuration" element={<Configuration />} />
                    <Route path="/users" element={<UserList />} />
                    <Route path="/roles" element={<RoleList />} />
                    <Route path="/api-keys" element={<ApiKeyList />} />
                    <Route path="/activity-logs" element={<ActivityLogList />} />
                    <Route path="/profile" element={<Profile />} />
                    <Route path="/workflows" element={<WorkflowList />} />
                    <Route path="/workflows/new" element={<WorkflowForm />} />
                    <Route path="/workflows/new/builder" element={<WorkflowBuilder />} />
                    <Route path="/workflows/:id" element={<WorkflowDetail />} />
                    <Route path="/workflows/:id/edit" element={<WorkflowForm />} />
                    <Route path="/workflows/:id/builder" element={<WorkflowBuilder />} />
                    <Route path="/workflows/:id/runs/:runId" element={<WorkflowRunDetail />} />
                    <Route path="/reports" element={<ReportDashboard />} />
                    <Route path="/reports/failureratetrend" element={<FailureRateTrendReport />} />
                    <Route path="/reports/percentiledurations" element={<PercentileDurationsReport />} />
                    <Route path="/reports/topslowjobs" element={<TopSlowJobsReport />} />
                    <Route path="/reports/workerthroughput" element={<WorkerThroughputReport />} />
                    <Route path="/reports/workerutilizationtrend" element={<WorkerUtilizationTrendReport />} />
                    <Route path="/reports/cronschedulevsactual" element={<CronScheduleVsActualReport />} />
                    <Route path="/reports/jobhealthscore" element={<JobHealthScoreReport />} />
                    <Route path="/reports/workflowsuccessrate" element={<WorkflowSuccessRateReport />} />
                    <Route path="/reports/workflowstepbottleneck" element={<WorkflowStepBottleneckReport />} />
                    <Route path="/reports/workflowdurationtrend" element={<WorkflowDurationTrendReport />} />

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
