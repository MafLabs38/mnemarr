import { Navigate, Route, Routes } from 'react-router-dom';

import AccountKeys from 'Accounts/AccountKeys';
import Accounts from 'Accounts/Accounts';
import MyAccount from 'Accounts/MyAccount';
import Overview from 'Overview/Overview';
import Status from 'System/Status/Status';

import NotFound from './NotFound';

export default function AppRoutes() {
  return (
    <Routes>
      <Route element={<Overview />} path="/" />
      <Route element={<Navigate replace to="/system/status" />} path="/system" />
      <Route element={<Status />} path="/system/status" />
      <Route element={<MyAccount />} path="/account" />
      <Route element={<Accounts />} path="/accounts" />
      <Route element={<AccountKeys />} path="/accounts/:id/keys" />
      <Route element={<NotFound />} path="*" />
    </Routes>
  );
}
