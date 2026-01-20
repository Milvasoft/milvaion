import { useState, useEffect, useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import Icon from '../components/Icon'
import api from '../services/api'
import './Tags.css'

function Tags() {
  const [tags, setTags] = useState([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const navigate = useNavigate()

  const loadTags = useCallback(async () => {
    try {
      setLoading(true)
      setError(null)
      const response = await api.get('/jobs/tags')
      const tagList = response?.data?.data || response?.data || []
      setTags(tagList)
    } catch (err) {
      setError('Failed to load tags')
      console.error(err)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    loadTags()
  }, [loadTags])

  const handleTagClick = (tag) => {
    navigate('/jobs', {
      state: {
        filterByTag: tag
      }
    })
  }

  if (loading) return <div className="loading">Loading tags...</div>
  if (error) return <div className="error">{error}</div>

  return (
    <div className="tags-page">
      <div className="page-header">
        <h1>
          <Icon name="label" size={28} />
          Job Tags
          <span>({tags.length})</span>
        </h1>
        <p className="subtitle">Click on a tag to view related jobs</p>
      </div>

      {tags.length === 0 ? (
        <div className="empty-state">
          <p>No tags found. Tags will appear here when you add them to jobs.</p>
        </div>
      ) : (
        <div className="tags-grid">
          {tags.map((tag, index) => (
            <button
              key={index}
              className="tag-card"
              onClick={() => handleTagClick(tag)}
            >
              <Icon name="label" size={24} className="tag-icon" />
              <span className="tag-name">{tag}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

export default Tags
