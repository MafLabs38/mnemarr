import { Navigate, Route, Routes } from 'react-router-dom';

import AccountKeys from 'Accounts/AccountKeys';
import Accounts from 'Accounts/Accounts';
import MyAccount from 'Accounts/MyAccount';
import Notes from 'Notes/Notes';
import Overview from 'Overview/Overview';
import RequirePermission from 'Session/RequirePermission';
import { permissions } from 'Session/session';
import Sources from 'Sources/Sources';
import Status from 'System/Status/Status';

import NotFound from './NotFound';

export default function AppRoutes() {
  return (
    <Routes>
      <Route element={<Overview />} path="/" />
      <Route element={<Navigate replace to="/system/status" />} path="/system" />
      <Route
        element={
          <RequirePermission permission={permissions.systemRead}>
            <Status />
          </RequirePermission>
        }
        path="/system/status"
      />
      <Route
        element={
          <RequirePermission permission={permissions.notesRead}>
            <Notes />
          </RequirePermission>
        }
        path="/notes"
      />
      <Route
        element={
          <RequirePermission permission={permissions.sourcesManage}>
            <Sources />
          </RequirePermission>
        }
        path="/sources"
      />
      <Route element={<MyAccount />} path="/account" />
      <Route
        element={
          <RequirePermission permission={permissions.accountsManage}>
            <Accounts />
          </RequirePermission>
        }
        path="/accounts"
      />
      <Route
        element={
          <RequirePermission permission={permissions.accountsManage}>
            <AccountKeys />
          </RequirePermission>
        }
        path="/accounts/:id/keys"
      />
      <Route element={<NotFound />} path="*" />
    </Routes>
  );
}
