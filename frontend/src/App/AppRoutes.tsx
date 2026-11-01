import { Navigate, Route, Routes } from 'react-router-dom';

import Overview from 'Overview/Overview';
import Status from 'System/Status/Status';

import NotFound from './NotFound';

export default function AppRoutes() {
  return (
    <Routes>
      <Route element={<Overview />} path="/" />
      <Route element={<Navigate replace to="/system/status" />} path="/system" />
      <Route element={<Status />} path="/system/status" />
      <Route element={<NotFound />} path="*" />
    </Routes>
  );
}
