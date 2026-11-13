# Milvaion UI - React SPA

Frontend dashboard for Milvaion Job Scheduler (served from API like Hangfire/TickerQ).

---

## 🚀 **Development**

Run frontend and backend separately (hot reload):

### **Terminal 1: API**
```bash
cd src/Milvaion.Api
dotnet run
# API: http://localhost:5000
```

### **Terminal 2: Frontend (Vite)**
```bash
cd src/MilvaionUI
npm install
npm run dev
# UI: http://localhost:3000
# Proxies /api → http://localhost:5000
```

---

## 📦 **Production Build**

### **Option 1: Manual Build**
```bash
cd src/MilvaionUI
npm install
npm run build
# Output: dist/
```

Then copy `dist/` → `src/Milvaion.Api/wwwroot/`

### **Option 2: Automatic Build (Release)**
```bash
cd src/Milvaion.Api
dotnet publish -c Release
# Automatically builds React app
# Output: bin/Release/net11.0/publish/wwwroot/
```

---

## 🌐 **Deployment**

### **Production URL Structure**
```
https://your-api.com/           → React SPA (index.html)
https://your-api.com/jobs       → React SPA (client-side routing)
https://your-api.com/api/jobs   → API endpoint
https://your-api.com/hubs/jobs  → SignalR hub
```

---

## 🛠️ **Tech Stack**

- **Framework:** React 18 + Vite
- **Routing:** React Router v6
- **State:** useState/useEffect hooks
- **Real-time:** SignalR (@microsoft/signalr)
- **Styling:** Plain CSS (dark theme)
- **Icons:** Material Icons

---

## 📁 **Project Structure**

```
src/MilvaionUI/
├── dist/                  # Build output (copied to API wwwroot)
├── public/                # Static assets
├── src/
│   ├── components/        # Reusable components
│   ├── pages/             # Route pages
│   │   ├── Jobs/
│   │   ├── Occurrences/
│   │   ├── Workers/
│   │   └── FailedOccurrences/
│   ├── services/          # API clients
│   ├── hooks/             # Custom hooks
│   ├── utils/             # Utilities
│   └── App.jsx            # Main app component
├── index.html
├── vite.config.js
└── package.json
```

---

## ⚙️ **Configuration**

### **API Base URL**

Development (Vite proxy handles this):
```javascript
// services/api.js
const API_URL = '/api'  // → http://localhost:5000/api
```

Production (same-origin):
```javascript
const API_URL = '/api'  // → https://your-api.com/api
```

### **SignalR Hub**

```javascript
// services/signalRService.js
const hubUrl = '/hubs/jobs'  // Works in both dev & prod
```

---

## 🎨 **Features**

✅ **Job Management** - Create, edit, trigger, view jobs  
✅ **Execution History** - Real-time occurrence tracking  
✅ **Worker Monitoring** - Live worker status & capacity  
✅ **Failed Jobs (DLQ)** - Review & resolve failures  
✅ **Live Updates** - SignalR real-time notifications  
✅ **Dark Theme** - Modern UI with gradients  
✅ **Responsive** - Works on desktop & mobile  
✅ **Job Versioning** - Track job definition changes  

---

## 📝 **Notes**

- **No CORS in Production:** Frontend served from same origin as API
- **Vite Dev Server:** Port 3000 (configurable in vite.config.js)
- **Build Output:** Optimized chunks (react-vendor, signalr)
- **Fallback Routing:** `app.MapFallbackToFile("index.html")` handles client-side routes
- **Hangfire Style:** Serve UI from `/` like Hangfire Dashboard

---

Built with ❤️ by Milvasoft
