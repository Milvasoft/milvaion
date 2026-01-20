import { useState, useEffect } from 'react'
import { Modal, Form, Input, Select, DatePicker, Radio, Switch, Button, message } from 'antd'
import jobService from '../services/jobService'
import workerService from '../services/workerService'
import moment from 'moment'

const { TextArea } = Input
const { Option } = Select

export default function JobFormDialog({ visible, job, onClose, onSuccess }) {
  const [form] = Form.useForm()
  const [loading, setLoading] = useState(false)
  const [workers, setWorkers] = useState([])
  const [selectedWorker, setSelectedWorker] = useState(null)
  const [scheduleType, setScheduleType] = useState('once')

  const isEdit = !!job

  useEffect(() => {
    if (visible) {
      loadWorkers()
      if (isEdit) {
        form.setFieldsValue({
          displayName: job.displayName,
          workerId: job.workerId,
          selectedJobName: job.jobNameInWorker,
          jobData: job.jobData || '{}',
          executeAt: job.executeAt ? moment(job.executeAt) : moment().add(1, 'hour'),
          cronExpression: job.cronExpression || '',
          isActive: job.isActive
        })
        setScheduleType(job.cronExpression ? 'recurring' : 'once')
      } else {
        form.resetFields()
        form.setFieldsValue({
          jobData: '{}',
          executeAt: moment().add(1, 'hour'),
          isActive: true
        })
        setScheduleType('once')
      }
    }
  }, [visible, job, isEdit, form])

  const loadWorkers = async () => {
    try {
      const response = await workerService.getAll()
      if (response.data.isSuccess) {
        const activeWorkers = response.data.data.filter(w => w.status === 'Active')
        setWorkers(activeWorkers)
      }
    } catch (error) {
      console.error('Failed to load workers:', error)
      message.error('Failed to load workers')
    }
  }

  const handleWorkerChange = (workerId) => {
    const worker = workers.find(w => w.workerId === workerId)
    setSelectedWorker(worker)
    form.setFieldsValue({ selectedJobName: undefined })

    // Auto-select if only one job type
    if (worker && worker.jobNames && worker.jobNames.length === 1) {
      form.setFieldsValue({ selectedJobName: worker.jobNames[0] })
    }
  }

  const handleSubmit = async () => {
    try {
      const values = await form.validateFields()
      setLoading(true)

      const payload = {
        displayName: values.displayName,
        workerId: values.workerId,
        selectedJobName: values.selectedJobName,
        jobData: values.jobData,
        executeAt: scheduleType === 'once' ? values.executeAt.toISOString() : null,
        cronExpression: scheduleType === 'recurring' ? values.cronExpression : null,
        isActive: values.isActive
      }

      let response
      if (isEdit) {
        response = await jobService.update(job.id, payload)
      } else {
        response = await jobService.create(payload)
      }

      if (response.data.isSuccess) {
        message.success(isEdit ? 'Job updated successfully' : 'Job created successfully')
        onSuccess()
        onClose()
      } else {
        message.error(response.data.message || 'Operation failed')
      }
    } catch (error) {
      console.error('Submit error:', error)
      message.error('Failed to save job')
    } finally {
      setLoading(false)
    }
  }

  const availableJobTypes = selectedWorker?.jobNames || []

  return (
    <Modal
      title={isEdit ? 'Edit Job' : 'Create New Job'}
      open={visible}
      onCancel={onClose}
      onOk={handleSubmit}
      confirmLoading={loading}
      width={700}
    >
      <Form form={form} layout="vertical">
        <Form.Item
          name="displayName"
          label="Display Name"
          rules={[{ required: true, message: 'Display name is required' }]}
        >
          <Input placeholder="Enter job display name" />
        </Form.Item>

        <Form.Item
          name="workerId"
          label="Worker"
          rules={[{ required: true, message: 'Please select a worker' }]}
        >
          <Select
            placeholder="Select worker"
            onChange={handleWorkerChange}
            showSearch
            optionFilterProp="children"
          >
            {workers.map(worker => (
              <Option key={worker.workerId} value={worker.workerId}>
                {worker.displayName} ({worker.workerId}) - {worker.currentJobs}/{worker.maxParallelJobsPerWorker || '∞'}
              </Option>
            ))}
          </Select>
        </Form.Item>

        <Form.Item
          name="selectedJobName"
          label="Job Type"
          rules={[{ required: true, message: 'Please select a job type' }]}
        >
          <Select
            placeholder="Select job type"
            disabled={!selectedWorker}
          >
            {availableJobTypes.map(jobName => (
              <Option key={jobName} value={jobName}>
                {jobName}
              </Option>
            ))}
          </Select>
        </Form.Item>

        <Form.Item
          name="jobData"
          label="Job Data (JSON)"
          rules={[
            {
              validator: (_, value) => {
                try {
                  JSON.parse(value)
                  return Promise.resolve()
                } catch (e) {
                  return Promise.reject('Invalid JSON format')
                }
              }
            }
          ]}
        >
          <TextArea rows={4} placeholder='{"key": "value"}' />
        </Form.Item>

        <Form.Item label="Schedule Type">
          <Radio.Group value={scheduleType} onChange={(e) => setScheduleType(e.target.value)}>
            <Radio value="once">Run Once</Radio>
            <Radio value="recurring">Recurring (Cron)</Radio>
          </Radio.Group>
        </Form.Item>

        {scheduleType === 'once' && (
          <Form.Item
            name="executeAt"
            label="Execute At"
            rules={[{ required: true, message: 'Please select execution time' }]}
          >
            <DatePicker
              showTime
              format="YYYY-MM-DD HH:mm:ss"
              disabledDate={(current) => current && current < moment().startOf('day')}
              style={{ width: '100%' }}
            />
          </Form.Item>
        )}

        {scheduleType === 'recurring' && (
          <Form.Item
            name="cronExpression"
            label="Cron Expression"
            rules={[{ required: true, message: 'Cron expression is required' }]}
          >
            <Input placeholder="0 0 9 * * ? (Every day at 9 AM)" />
          </Form.Item>
        )}

        <Form.Item name="isActive" label="Active" valuePropName="checked">
          <Switch />
        </Form.Item>
      </Form>
    </Modal>
  )
}
