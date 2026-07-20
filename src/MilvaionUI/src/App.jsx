import { lazy, Suspense } from 'react'
import { SkeletonCard } from './components/Skeleton'
import { BrowserRouter as Router, Routes, Route, Navigate } from 'react-router-dom'
import { ThemeProvider } from './contexts/ThemeContext'
import Layout from './components/Layout'
import ProtectedRoute from './components/ProtectedRoute'
import Login from './pages/Login/Login'

/*
 * Pages are loaded on demand. Imported statically they all landed in one bundle,
 * and the heavy dependencies came with them - opening the dashboard downloaded
 * the workflow canvas (reactflow) and the report charts (recharts) too.
 *
 * Login and Layout stay eager: both are needed for the first paint, so deferring
 * them would only lengthen the blank screen.
 */
const ActivityLogList = lazy(() => import('./pages/UserManagement/ActivityLogList'))
/* `/admin` now serves the redesigned Monitoring page. `AdminDashboard` is left in the
   tree so switching back is this one line. */
const AdminDashboard = lazy(() => import('./pages/Admin/Monitoring'))
const ApiKeyList = lazy(() => import('./pages/UserManagement/ApiKeyList'))
/* The redesigned page lives in its own folder alongside the spec that drives it. The old
   `pages/Configuration.jsx` is untouched; switching back is this one line. */
const Configuration = lazy(() => import('./pages/Configuration/Configuration'))
const CronScheduleVsActualReport = lazy(() => import('./pages/Reports/CronScheduleVsActualReport'))
/* Redesigned dashboard. The old `pages/Dashboard.jsx` is untouched; this is the switch. */
const Dashboard = lazy(() => import('./pages/Dashboard/Dashboard'))
const ExecutionList = lazy(() => import('./pages/Executions/ExecutionList'))
const FailedOccurrenceDetail = lazy(() => import('./pages/FailedOccurrences/FailedOccurrenceDetail'))
const FailedOccurrenceList = lazy(() => import('./pages/FailedOccurrences/FailedOccurrenceList'))
const FailureRateTrendReport = lazy(() => import('./pages/Reports/FailureRateTrendReport'))
const JobDetail = lazy(() => import('./pages/Jobs/JobDetail'))
const JobForm = lazy(() => import('./pages/Jobs/JobForm'))
const JobHealthScoreReport = lazy(() => import('./pages/Reports/JobHealthScoreReport'))
const JobList = lazy(() => import('./pages/Jobs/JobList'))
const OccurrenceDetail = lazy(() => import('./pages/Occurrences/OccurrenceDetail'))
const PercentileDurationsReport = lazy(() => import('./pages/Reports/PercentileDurationsReport'))
const Profile = lazy(() => import('./pages/Profile/Profile'))
const ReportDashboard = lazy(() => import('./pages/Reports/ReportDashboard'))
const RoleList = lazy(() => import('./pages/UserManagement/RoleList'))
const Tags = lazy(() => import('./pages/Tags'))
const TopSlowJobsReport = lazy(() => import('./pages/Reports/TopSlowJobsReport'))
const UpcomingExecutions = lazy(() => import('./pages/UpcomingExecutions/UpcomingExecutions'))
const UserList = lazy(() => import('./pages/UserManagement/UserList'))
const WorkerList = lazy(() => import('./pages/Workers/WorkerList'))
const WorkerThroughputReport = lazy(() => import('./pages/Reports/WorkerThroughputReport'))
const WorkerUtilizationTrendReport = lazy(() => import('./pages/Reports/WorkerUtilizationTrendReport'))
const WorkflowBuilder = lazy(() => import('./pages/Workflows/WorkflowBuilder/WorkflowBuilder'))
const WorkflowDetail = lazy(() => import('./pages/Workflows/WorkflowDetail'))
const WorkflowDurationTrendReport = lazy(() => import('./pages/Reports/WorkflowDurationTrendReport'))
const WorkflowForm = lazy(() => import('./pages/Workflows/WorkflowForm'))
const WorkflowList = lazy(() => import('./pages/Workflows/WorkflowList'))
const WorkflowRunDetail = lazy(() => import('./pages/Workflows/WorkflowRunDetail'))
const WorkflowStepBottleneckReport = lazy(() => import('./pages/Reports/WorkflowStepBottleneckReport'))
const WorkflowSuccessRateReport = lazy(() => import('./pages/Reports/WorkflowSuccessRateReport'))

/**
 * Shown while a page chunk is downloading.
 *
 * Invisible on a local network; on a slow connection it is the difference between
 * a blank screen and something that looks like it is working.
 */
function PageFallback() {
  return (
    <div className="page">
      <SkeletonCard lines={6} />
    </div>
  )
}

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
                  <Suspense fallback={<PageFallback />}>
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
                  </Suspense>
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
