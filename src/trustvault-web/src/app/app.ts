import {
  Component,
  ChangeDetectorRef,
  inject,
  OnDestroy
} from '@angular/core';

import { CommonModule } from '@angular/common';

import {
  HttpClient,
  HttpErrorResponse,
  HttpHeaders
} from '@angular/common/http';

import {
  DomSanitizer,
  SafeResourceUrl
} from '@angular/platform-browser';

import { FormsModule } from '@angular/forms';

type DocumentSection = 'documents' | 'shared' | 'owner';
type AppPage = 'dashboard' | 'documents' | 'permissions' | 'audit' | 'recycle';
type SharePermission = 'Read' | 'Edit';

type DocumentRow = {
  id: string;
  name: string;
  type: 'PDF' | 'DOC' | 'XLS' | 'TXT';
  owner: string;
  updated: string;
  size: string;
  access: string;
  originalFileName?: string;
  contentType?: string;
  createdAtUtc?: string;
  updatedAtUtc?: string;
  sizeBytes?: number;
  apiAccess?: string;
  ownerEmail?: string;
  deletedAtUtc?: string | null;
};

type ApiDocument = {
  id: string;
  name?: string;
  originalFileName?: string;
  category?: string;
  contentType?: string;
  sizeBytes?: number;
  createdAtUtc?: string;
  updatedAtUtc?: string;
  access?: string;
  ownerEmail?: string;
  owner?: string;
  deletedAtUtc?: string | null;
};

type SharedAccess = {
  id: string;
  recipientEmail: string;
  permission: SharePermission;
  grantedAtUtc: string;
};

type PermissionRow = {
  id: string;
  documentId: string;
  documentName: string;
  recipientEmail: string;
  permission: SharePermission;
  grantedAtUtc: string;
  revokedAtUtc: string | null;
  status: 'Active' | 'Revoked';
  canRevoke: boolean;
};

type AuditRow = {
  id: string;
  action: string;
  outcome: string;
  createdAtUtc: string;
  requestId?: string | null;
  details?: string | null;
  actorEmail?: string | null;
  documentName?: string | null;
};

type LoginResponse = {
  accessToken: string;
  tokenType: string;
  expiresAtUtc: string;
  user: {
    id: string;
    email: string;
    role: string;
  };
};

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './app.html',
  styleUrls: ['./app.css']
})
export class App implements OnDestroy {
  private readonly http = inject(HttpClient);
  private readonly cdr = inject(ChangeDetectorRef);
  private readonly sanitizer = inject(DomSanitizer);

  private readonly apiUrl =
    window.location.hostname === 'localhost' ? 'http://localhost:5099' : '';

  private fileInput: HTMLInputElement | null = null;
  private previewObjectUrl: string | null = null;

  private documentMessageTimer?: ReturnType<typeof setTimeout>;
  private trashMessageTimer?: ReturnType<typeof setTimeout>;

  // LOGIN
  email = '';
  password = '';
  isLoggedIn = false;
  isRegisterMode = false;
  isSubmitting = false;
  loginError = '';
  userRole = '';
  accessToken = '';

  // FORGOT PASSWORD / EMAIL OTP
  authView: 'login' | 'forgot' | 'reset' = 'login';
  resetEmail = '';
  resetOtp = '';
  newPassword = '';
  confirmNewPassword = '';
  isSendingOtp = false;
  isResettingPassword = false;
  resetError = '';
  resetMessage = '';

  // PAGE NAVIGATION
  activePage: AppPage = 'dashboard';
  activeSection: DocumentSection = 'documents';

  // DOCUMENTS
  documents: DocumentRow[] = [];
  searchTerm = '';
  isLoadingDocuments = false;
  documentError = '';
  documentMessage = '';

  // RECYCLE BIN
  trashedDocuments: DocumentRow[] = [];
  isLoadingTrash = false;
  trashError = '';
  trashMessage = '';
  restoringDocumentId = '';
  permanentlyDeletingDocumentId = '';

  // UPLOAD
  isUploading = false;
  selectedFile: File | null = null;
  uploadError = '';
  uploadMessage = '';

  // DOCUMENT DETAILS AND PREVIEW
  selectedDocument: DocumentRow | null = null;
  showDocumentDetails = false;
  previewUrl: SafeResourceUrl | null = null;
  previewLoading = false;
  previewError = '';

  // SHARING
  showShareDialog = false;
  shareTargetDocument: DocumentRow | null = null;
  shareEmail = '';
  sharePermission: SharePermission = 'Read';
  isSharing = false;
  isLoadingShares = false;
  shareError = '';
  shareMessage = '';
  existingShares: SharedAccess[] = [];

  // ACCESS PERMISSIONS
  permissions: PermissionRow[] = [];
  isLoadingPermissions = false;
  permissionsError = '';
  permissionMessage = '';

  // AUDIT ACTIVITY
  auditEvents: AuditRow[] = [];
  isLoadingAudit = false;
  auditError = '';

  // CLEAR DOCUMENT SUCCESS MESSAGE TIMER
  private clearDocumentMessageTimer(): void {
    if (this.documentMessageTimer !== undefined) {
      clearTimeout(this.documentMessageTimer);
      this.documentMessageTimer = undefined;
    }
  }

  // CLEAR RECYCLE BIN SUCCESS MESSAGE TIMER
  private clearTrashMessageTimer(): void {
    if (this.trashMessageTimer !== undefined) {
      clearTimeout(this.trashMessageTimer);
      this.trashMessageTimer = undefined;
    }
  }

  // SHOW DOCUMENT SUCCESS MESSAGE FOR 3 SECONDS
  private showDocumentSuccessMessage(message: string): void {
    this.clearDocumentMessageTimer();
    this.documentMessage = message;

    this.documentMessageTimer = setTimeout(() => {
      this.documentMessage = '';
      this.documentMessageTimer = undefined;
      this.cdr.markForCheck();
    }, 3000);
  }

  // SHOW RECYCLE BIN SUCCESS MESSAGE FOR 3 SECONDS
  private showTrashSuccessMessage(message: string): void {
    this.clearTrashMessageTimer();
    this.trashMessage = message;

    this.trashMessageTimer = setTimeout(() => {
      this.trashMessage = '';
      this.trashMessageTimer = undefined;
      this.cdr.markForCheck();
    }, 3000);
  }

  // CLEAN UP TIMERS AND PREVIEW URL
  ngOnDestroy(): void {
    this.clearDocumentMessageTimer();
    this.clearTrashMessageTimer();
    this.releasePreviewUrl();
  }

  // RECYCLE BIN NAVIGATION
  openRecycleBin(): void {
    this.clearTrashMessageTimer();
    this.activePage = 'recycle';
    this.trashError = '';
    this.trashMessage = '';
    this.searchTerm = '';
    this.loadTrash();
    this.cdr.markForCheck();
  }

  // LOAD RECYCLE BIN
  loadTrash(): void {
    if (!this.accessToken) {
      this.trashError = 'Please sign in to view the Recycle Bin.';
      this.isLoadingTrash = false;
      this.cdr.markForCheck();
      return;
    }

    this.isLoadingTrash = true;
    this.trashError = '';

    this.http.get<ApiDocument[]>(
      `${this.apiUrl}/api/documents/trash`,
      {
        headers: this.authHeaders(),
        timeout: 15000
      }
    ).subscribe({
      next: rows => {
        this.trashedDocuments = (rows ?? []).map(row => {
          const fileName =
            row.originalFileName || row.name || 'Document';

          return {
            id: row.id,
            name: fileName,
            originalFileName: row.originalFileName,
            contentType: row.contentType,
            createdAtUtc: row.createdAtUtc,
            updatedAtUtc: row.updatedAtUtc,
            deletedAtUtc: row.deletedAtUtc,
            sizeBytes: row.sizeBytes,
            apiAccess: 'Owner',
            ownerEmail: this.email,
            type: this.getDocumentType(fileName),
            owner: 'You',
            updated: this.formatDate(
              row.deletedAtUtc || row.updatedAtUtc
            ),
            access: 'Private',
            size: this.formatFileSize(row.sizeBytes ?? 0)
          };
        });

        this.isLoadingTrash = false;
        this.cdr.markForCheck();
      },
      error: (error: HttpErrorResponse) => {
        this.isLoadingTrash = false;

        this.trashError = error.status === 401
          ? 'Your session expired. Please sign in again.'
          : 'Could not load the Recycle Bin. Please try again.';

        this.cdr.markForCheck();
      }
    });
  }

  // RESTORE DOCUMENT
  restoreDocument(doc: DocumentRow): void {
    if (!this.accessToken || this.restoringDocumentId) return;

    this.clearTrashMessageTimer();
    this.restoringDocumentId = doc.id;
    this.trashError = '';
    this.trashMessage = '';

    this.http.post<{ message?: string }>(
      `${this.apiUrl}/api/documents/${encodeURIComponent(doc.id)}/restore`,
      {},
      {
        headers: this.authHeaders(),
        timeout: 15000
      }
    ).subscribe({
      next: result => {
        this.restoringDocumentId = '';

        this.trashedDocuments = this.trashedDocuments.filter(
          item => item.id !== doc.id
        );

        this.showTrashSuccessMessage(
          result?.message || 'Document restored successfully.'
        );

        this.loadDocuments();
        this.cdr.markForCheck();
      },
      error: (error: HttpErrorResponse) => {
        this.restoringDocumentId = '';
        this.trashError = error.error?.message ||
          'Could not restore this document.';
        this.cdr.markForCheck();
      }
    });
  }

  // PERMANENT DELETE DOCUMENT
  permanentDeleteDocument(doc: DocumentRow): void {
    if (!this.accessToken || this.permanentlyDeletingDocumentId) return;

    this.clearTrashMessageTimer();
    this.permanentlyDeletingDocumentId = doc.id;
    this.trashError = '';
    this.trashMessage = '';

    this.http.delete<{ message?: string }>(
      `${this.apiUrl}/api/documents/${encodeURIComponent(doc.id)}/permanent`,
      {
        headers: this.authHeaders(),
        timeout: 15000
      }
    ).subscribe({
      next: result => {
        this.permanentlyDeletingDocumentId = '';

        this.trashedDocuments = this.trashedDocuments.filter(
          item => item.id !== doc.id
        );

        this.showTrashSuccessMessage(
          result?.message || 'Document permanently deleted.'
        );

        this.cdr.markForCheck();
      },
      error: (error: HttpErrorResponse) => {
        this.permanentlyDeletingDocumentId = '';
        this.trashError = error.error?.message ||
          'Could not permanently delete this document.';
        this.cdr.markForCheck();
      }
    });
  }

  // DOCUMENT FILTERING
  get filteredDocuments(): DocumentRow[] {
    let rows = this.documents;

    if (this.activeSection === 'shared') {
      rows = rows.filter(doc => doc.access === 'Shared');
    } else if (this.activeSection === 'owner') {
      rows = rows.filter(doc => this.isDocumentOwner(doc));
    }

    const query = this.searchTerm.trim().toLowerCase();

    if (!query) return rows;

    return rows.filter(doc =>
      [doc.name, doc.owner, doc.type, doc.access, doc.size]
        .join(' ')
        .toLowerCase()
        .includes(query)
    );
  }

  get filteredTrashDocuments(): DocumentRow[] {
    const query = this.searchTerm.trim().toLowerCase();

    if (!query) return this.trashedDocuments;

    return this.trashedDocuments.filter(doc =>
      [doc.name, doc.owner, doc.type, doc.size]
        .join(' ')
        .toLowerCase()
        .includes(query)
    );
  }

  get recycleBinCount(): number {
    return this.trashedDocuments.length;
  }

  // DASHBOARD COUNTS
  get totalDocumentsCount(): number {
    return this.documents.length;
  }

  get sharedDocumentsCount(): number {
    return this.documents.filter(doc => doc.access === 'Shared').length;
  }

  get ownedDocumentsCount(): number {
    return this.documents.filter(doc => this.isDocumentOwner(doc)).length;
  }

  // NAVIGATION
  selectSection(section: DocumentSection): void {
    this.activeSection = section;
    this.activePage = 'documents';
    this.searchTerm = '';
    this.documentError = '';
    this.cdr.markForCheck();
  }

  goToDashboard(): void {
    this.activePage = 'dashboard';
    this.activeSection = 'documents';
    this.searchTerm = '';
    this.documentError = '';
    this.cdr.markForCheck();
  }

  // PERMISSIONS PAGE
  openPermissionsPage(): void {
    this.activePage = 'permissions';
    this.permissionsError = '';
    this.permissionMessage = '';
    this.loadPermissions();
    this.cdr.markForCheck();
  }

  loadPermissions(): void {
    if (!this.accessToken) {
      this.permissionsError = 'Please sign in to view permissions.';
      this.isLoadingPermissions = false;
      this.cdr.markForCheck();
      return;
    }

    this.isLoadingPermissions = true;
    this.permissionsError = '';

    this.http.get<PermissionRow[]>(
      `${this.apiUrl}/api/access/permissions`,
      {
        headers: this.authHeaders(),
        timeout: 15000
      }
    ).subscribe({
      next: rows => {
        this.permissions = rows ?? [];
        this.isLoadingPermissions = false;
        this.cdr.markForCheck();
      },
      error: (error: HttpErrorResponse) => {
        this.isLoadingPermissions = false;

        this.permissionsError = error.status === 401
          ? 'Your session expired. Please sign in again.'
          : 'Could not load access permissions. Please try again.';

        this.cdr.markForCheck();
      }
    });
  }

  // REVOKE PERMISSION
  revokePermission(permission: PermissionRow): void {
    if (permission.status !== 'Active') {
      this.permissionsError =
        'This permission has already been revoked.';
      this.cdr.markForCheck();
      return;
    }

    if (!this.accessToken) {
      this.permissionsError =
        'Please sign in again to revoke access.';
      this.cdr.markForCheck();
      return;
    }

    this.permissionsError = '';
    this.permissionMessage = '';

    this.http.delete<{ message?: string }>(
      `${this.apiUrl}/api/documents/${encodeURIComponent(permission.documentId)}/shares/${encodeURIComponent(permission.id)}`,
      {
        headers: this.authHeaders(),
        timeout: 15000
      }
    ).subscribe({
      next: result => {
        this.permissionMessage =
          result?.message || 'Document access revoked successfully.';

        this.loadPermissions();
        this.loadDocuments();
        this.cdr.markForCheck();
      },
      error: (error: HttpErrorResponse) => {
        if (error.status === 401) {
          this.permissionsError =
            'Your session expired. Please sign in again.';
        } else if (error.status === 403) {
          this.permissionsError = error.error?.message ||
            'You do not have permission to revoke this access. Only the document owner can revoke it.';
        } else if (error.status === 404) {
          this.permissionsError = error.error?.message ||
            'The document share was not found or was already revoked.';
        } else if (error.status === 0) {
          this.permissionsError =
            'Cannot connect to the backend. Check that the API is running on port 5099.';
        } else {
          this.permissionsError = error.error?.message ||
            'Could not revoke access. Please try again.';
        }

        this.cdr.markForCheck();
      }
    });
  }

  // AUDIT PAGE
  openAuditPage(): void {
    this.activePage = 'audit';
    this.auditError = '';
    this.loadAudit();
    this.cdr.markForCheck();
  }

  loadAudit(): void {
    if (!this.accessToken) {
      this.auditError = 'Please sign in to view audit activity.';
      this.isLoadingAudit = false;
      this.cdr.markForCheck();
      return;
    }

    this.isLoadingAudit = true;
    this.auditError = '';

    this.http.get<AuditRow[]>(
      `${this.apiUrl}/api/audit`,
      {
        headers: this.authHeaders(),
        timeout: 15000
      }
    ).subscribe({
      next: rows => {
        this.auditEvents = rows ?? [];
        this.isLoadingAudit = false;
        this.cdr.markForCheck();
      },
      error: (error: HttpErrorResponse) => {
        this.isLoadingAudit = false;

        this.auditError = error.status === 401
          ? 'Your session expired. Please sign in again.'
          : 'Could not load audit activity. Please try again.';

        this.cdr.markForCheck();
      }
    });
  }

  // AUTH MODE
  toggleAuthMode(): void {
    this.authView = 'login';
    this.isRegisterMode = !this.isRegisterMode;
    this.loginError = '';
    this.password = '';
    this.resetError = '';
    this.resetMessage = '';
    this.cdr.markForCheck();
  }

  // FORGOT PASSWORD
  openForgotPassword(): void {
    this.authView = 'forgot';
    this.resetEmail = this.email.trim();
    this.resetOtp = '';
    this.newPassword = '';
    this.confirmNewPassword = '';
    this.resetError = '';
    this.resetMessage = '';
    this.loginError = '';
    this.cdr.markForCheck();
  }

  sendResetOtp(): void {
    if (this.isSendingOtp) return;

    const email = this.resetEmail.trim().toLowerCase();

    if (!email) {
      this.resetError = 'Please enter your email address.';
      this.cdr.markForCheck();
      return;
    }

    this.isSendingOtp = true;
    this.resetError = '';
    this.resetMessage = '';

    this.http.post<{ message?: string }>(
      `${this.apiUrl}/api/auth/forgot-password`,
      { email },
      { timeout: 20000 }
    ).subscribe({
      next: result => {
        this.isSendingOtp = false;
        this.resetEmail = email;
        this.authView = 'reset';

        this.resetMessage = result?.message ??
          'If the account exists, a verification code has been sent.';

        this.cdr.markForCheck();
      },
      error: (error: HttpErrorResponse) => {
        this.isSendingOtp = false;

        this.resetError = error.status === 0
          ? 'Cannot connect to the backend. Check port 5099.'
          : error.error?.message ??
            'Could not send the verification code. Please try again.';

        this.cdr.markForCheck();
      }
    });
  }

  resetPassword(): void {
    if (this.isResettingPassword) return;

    const email = this.resetEmail.trim().toLowerCase();
    const otp = this.resetOtp.trim();

    if (!email || !otp || !this.newPassword || !this.confirmNewPassword) {
      this.resetError = 'Please complete all fields.';
      this.cdr.markForCheck();
      return;
    }

    if (!/^\d{6}$/.test(otp)) {
      this.resetError = 'Enter the 6-digit verification code.';
      this.cdr.markForCheck();
      return;
    }

    if (this.newPassword.length < 8 || this.newPassword.length > 128) {
      this.resetError =
        'Your new password must have 8 to 128 characters.';
      this.cdr.markForCheck();
      return;
    }

    if (this.newPassword !== this.confirmNewPassword) {
      this.resetError = 'The passwords do not match.';
      this.cdr.markForCheck();
      return;
    }

    this.isResettingPassword = true;
    this.resetError = '';
    this.resetMessage = '';

    this.http.post<{ message?: string }>(
      `${this.apiUrl}/api/auth/reset-password`,
      {
        email,
        otp,
        newPassword: this.newPassword
      },
      { timeout: 20000 }
    ).subscribe({
      next: result => {
        this.isResettingPassword = false;
        this.authView = 'login';
        this.isRegisterMode = false;
        this.email = email;
        this.password = '';
        this.resetOtp = '';
        this.newPassword = '';
        this.confirmNewPassword = '';
        this.loginError = '';
        this.resetError = '';

        this.resetMessage = result?.message ??
          'Password reset successfully. Please sign in with your new password.';

        this.cdr.markForCheck();
      },
      error: (error: HttpErrorResponse) => {
        this.isResettingPassword = false;

        this.resetError = error.status === 0
          ? 'Cannot connect to the backend. Check port 5099.'
          : error.error?.message ??
            'Could not reset the password. Check the code and try again.';

        this.cdr.markForCheck();
      }
    });
  }

  backToLogin(): void {
    this.authView = 'login';
    this.isRegisterMode = false;
    this.resetOtp = '';
    this.newPassword = '';
    this.confirmNewPassword = '';
    this.resetError = '';
    this.resetMessage = '';
    this.loginError = '';
    this.cdr.markForCheck();
  }

  // LOGIN AND REGISTER
  openPreview(): void {
    if (this.isSubmitting) return;

    this.loginError = '';

    if (!this.email.trim() || !this.password) {
      this.loginError = 'Enter your email and password.';
      this.cdr.markForCheck();
      return;
    }

    if (this.isRegisterMode && this.password.length < 8) {
      this.loginError =
        'Choose a password with at least 8 characters.';
      this.cdr.markForCheck();
      return;
    }

    this.isSubmitting = true;
    this.cdr.markForCheck();

    const endpoint = this.isRegisterMode
      ? '/api/auth/register'
      : '/api/auth/login';

    this.http.post<LoginResponse>(
      `${this.apiUrl}${endpoint}`,
      {
        email: this.email.trim(),
        password: this.password
      },
      { timeout: 15000 }
    ).subscribe({
      next: result => {
        this.accessToken = result.accessToken;
        this.email = result.user.email;
        this.userRole = result.user.role;
        this.isLoggedIn = true;
        this.isSubmitting = false;
        this.password = '';
        this.loginError = '';
        this.activePage = 'dashboard';
        this.activeSection = 'documents';
        this.searchTerm = '';
        this.permissions = [];
        this.auditEvents = [];

        this.cdr.markForCheck();
        this.loadDocuments();
      },
      error: (error: HttpErrorResponse) => {
        this.isSubmitting = false;

        if (error.status === 0) {
          this.loginError =
            'Cannot connect to backend. Check port 5099.';
        } else if (error.status === 401) {
          this.loginError = 'Incorrect email or password.';
        } else if (error.status === 409) {
          this.loginError = error.error?.message ??
            'Account setup is already complete. Please sign in.';

          if (this.loginError.includes('already exists') ||
              this.loginError.includes('Account creation is closed')) {
            this.isRegisterMode = false;
          }
        } else if (error.status === 404 && this.isRegisterMode) {
          this.loginError =
            'Account creation is unavailable on this deployment. Please try again later.';
        } else if (error.status === 400) {
          this.loginError = error.error?.message ??
            'Please check your email and password.';
        } else {
          this.loginError = 'The request failed. Please try again.';
        }

        this.cdr.markForCheck();
      }
    });
  }

  private authHeaders(): HttpHeaders {
    return new HttpHeaders({
      Authorization: `Bearer ${this.accessToken}`
    });
  }

  // LOAD DOCUMENTS
  loadDocuments(): void {
    if (!this.accessToken) return;

    this.isLoadingDocuments = true;
    this.documentError = '';

    this.http.get<ApiDocument[]>(
      `${this.apiUrl}/api/documents`,
      {
        headers: this.authHeaders(),
        timeout: 15000
      }
    ).subscribe({
      next: rows => {
        this.documents = (rows ?? []).map(row => {
          const apiAccess = row.access ?? 'Unknown';
          const isOwned = apiAccess.toLowerCase() === 'owner';
          const fileName =
            row.originalFileName || row.name || 'Document';

          return {
            id: row.id,
            name: fileName,
            originalFileName: row.originalFileName,
            contentType: row.contentType,
            createdAtUtc: row.createdAtUtc,
            updatedAtUtc: row.updatedAtUtc,
            sizeBytes: row.sizeBytes,
            apiAccess,
            ownerEmail: row.ownerEmail,
            deletedAtUtc: row.deletedAtUtc,
            type: this.getDocumentType(fileName),
            owner: isOwned
              ? 'You'
              : row.ownerEmail || row.owner || 'Shared with you',
            updated: this.formatDate(
              row.updatedAtUtc || row.createdAtUtc
            ),
            access: isOwned ? 'Private' : 'Shared',
            size: this.formatFileSize(row.sizeBytes ?? 0)
          };
        });

        this.isLoadingDocuments = false;
        this.cdr.markForCheck();
      },
      error: (error: HttpErrorResponse) => {
        this.isLoadingDocuments = false;

        if (error.status === 401) {
          this.logout();
          this.loginError =
            'Your session expired. Please sign in again.';
        } else {
          this.documentError =
            'Could not load documents from the backend.';
        }

        this.cdr.markForCheck();
      }
    });
  }

  // DOCUMENT OWNERSHIP
  isDocumentOwner(doc: DocumentRow): boolean {
    return (doc.apiAccess ?? '').toLowerCase() === 'owner' ||
      doc.owner === 'You';
  }

  // DOCUMENT DETAILS
  getDocumentName(doc: DocumentRow): string {
    return doc.originalFileName || doc.name || 'Document';
  }

  getDocumentOwner(doc: DocumentRow): string {
    if (doc.ownerEmail) return doc.ownerEmail;

    return doc.owner === 'You'
      ? this.email || 'You'
      : doc.owner || 'Not available';
  }

  getDocumentDate(doc: DocumentRow): string {
    return doc.updated ||
      this.formatDate(doc.updatedAtUtc || doc.createdAtUtc);
  }

  getDocumentAccess(doc: DocumentRow): string {
    const permission = (doc.apiAccess ?? '').toLowerCase();

    if (permission === 'owner') return 'Owner — private document';
    if (permission === 'read') return 'Shared — read access';
    if (permission === 'edit') return 'Shared — edit access';

    return doc.access;
  }

  // DOCUMENT PREVIEW
  isPreviewableDocument(doc: DocumentRow): boolean {
    const name = this.getDocumentName(doc).toLowerCase();

    return name.endsWith('.pdf') || name.endsWith('.txt');
  }

  openDocumentDetails(doc: DocumentRow): void {
    this.selectedDocument = doc;
    this.showDocumentDetails = true;
    this.previewUrl = null;
    this.previewError = '';
    this.previewLoading = false;
    this.releasePreviewUrl();

    if (!this.accessToken) {
      this.previewError =
        'Please sign in again to view this document.';
      this.cdr.markForCheck();
      return;
    }

    if (!this.isPreviewableDocument(doc)) {
      this.previewError =
        'Preview is available for PDF and TXT files only. Download other file types.';
      this.cdr.markForCheck();
      return;
    }

    this.previewLoading = true;
    this.cdr.markForCheck();

    this.http.get(
      `${this.apiUrl}/api/documents/${encodeURIComponent(doc.id)}/download`,
      {
        headers: this.authHeaders(),
        responseType: 'blob',
        timeout: 30000
      }
    ).subscribe({
      next: fileBlob => {
        const name = this.getDocumentName(doc).toLowerCase();

        const mimeType = name.endsWith('.pdf')
          ? 'application/pdf'
          : 'text/plain';

        const previewBlob = new Blob([fileBlob], {
          type: mimeType
        });

        this.releasePreviewUrl();
        this.previewObjectUrl = URL.createObjectURL(previewBlob);

        this.previewUrl =
          this.sanitizer.bypassSecurityTrustResourceUrl(
            this.previewObjectUrl
          );

        this.previewLoading = false;
        this.cdr.markForCheck();
      },
      error: (error: HttpErrorResponse) => {
        this.previewLoading = false;

        this.previewError = error.status === 403
          ? 'You do not have permission to open this document.'
          : 'Unable to open the document. Please try again.';

        this.cdr.markForCheck();
      }
    });
  }

  closeDocumentDetails(): void {
    this.showDocumentDetails = false;
    this.selectedDocument = null;
    this.previewUrl = null;
    this.previewLoading = false;
    this.previewError = '';
    this.releasePreviewUrl();
    this.cdr.markForCheck();
  }

  private releasePreviewUrl(): void {
    if (this.previewObjectUrl) {
      URL.revokeObjectURL(this.previewObjectUrl);
      this.previewObjectUrl = null;
    }
  }

  // SHARED DOCUMENTS
  openShareDialog(doc: DocumentRow): void {
    if (!this.isDocumentOwner(doc)) {
      this.documentError =
        'Only the document owner can manage sharing.';
      this.cdr.markForCheck();
      return;
    }

    this.shareTargetDocument = doc;
    this.showShareDialog = true;
    this.shareEmail = '';
    this.sharePermission = 'Read';
    this.shareError = '';
    this.shareMessage = '';
    this.existingShares = [];

    this.loadShares(doc);
    this.cdr.markForCheck();
  }

  loadShares(doc: DocumentRow): void {
    if (!this.accessToken) {
      this.shareError =
        'Please sign in again to view sharing details.';
      this.cdr.markForCheck();
      return;
    }

    this.isLoadingShares = true;
    this.shareError = '';

    this.http.get<SharedAccess[]>(
      `${this.apiUrl}/api/documents/${encodeURIComponent(doc.id)}/shares`,
      {
        headers: this.authHeaders(),
        timeout: 15000
      }
    ).subscribe({
      next: shares => {
        this.existingShares = shares ?? [];
        this.isLoadingShares = false;
        this.cdr.markForCheck();
      },
      error: (error: HttpErrorResponse) => {
        this.isLoadingShares = false;
        this.shareError = error.error?.message ??
          'Could not load document sharing information.';
        this.cdr.markForCheck();
      }
    });
  }

  shareDocumentWithUser(): void {
    const doc = this.shareTargetDocument;

    if (this.isSharing || !doc) return;

    if (!this.isDocumentOwner(doc)) {
      this.shareError =
        'Only the document owner can share this document.';
      this.cdr.markForCheck();
      return;
    }

    const recipientEmail = this.shareEmail.trim().toLowerCase();

    if (!recipientEmail) {
      this.shareError = 'Enter the recipient email address.';
      this.cdr.markForCheck();
      return;
    }

    if (recipientEmail === this.email.toLowerCase()) {
      this.shareError =
        'You cannot share a document with your own account.';
      this.cdr.markForCheck();
      return;
    }

    this.isSharing = true;
    this.shareError = '';
    this.shareMessage = '';

    this.http.post<{ message?: string; share: SharedAccess }>(
      `${this.apiUrl}/api/documents/${encodeURIComponent(doc.id)}/shares`,
      {
        email: recipientEmail,
        permission: this.sharePermission
      },
      {
        headers: this.authHeaders(),
        timeout: 15000
      }
    ).subscribe({
      next: result => {
        this.isSharing = false;
        this.shareMessage =
          result.message || 'Document shared successfully.';
        this.shareEmail = '';

        this.loadShares(doc);
        this.cdr.markForCheck();
      },
      error: (error: HttpErrorResponse) => {
        this.isSharing = false;

        if (error.status === 401) {
          this.shareError =
            'Your session expired. Please sign in again.';
        } else if (error.status === 403) {
          this.shareError =
            'Only the document owner can share this document.';
        } else if (error.status === 404) {
          this.shareError = error.error?.message ??
            'Document or recipient account not found. The recipient must register first.';
        } else if (error.status === 400) {
          this.shareError = error.error?.message ??
            'Check the recipient email and permission.';
        } else if (error.status === 0) {
          this.shareError = 'Cannot connect to the backend.';
        } else {
          this.shareError = error.error?.message ??
            'Sharing failed. Please try again.';
        }

        this.cdr.markForCheck();
      }
    });
  }

  // REVOKE SHARED ACCESS
  revokeShare(share: SharedAccess): void {
    const doc = this.shareTargetDocument;

    if (!doc || !this.isDocumentOwner(doc)) {
      this.shareError =
        'Only the document owner can revoke access.';
      this.cdr.markForCheck();
      return;
    }

    this.shareError = '';
    this.shareMessage = '';

    this.http.delete<{ message?: string }>(
      `${this.apiUrl}/api/documents/${encodeURIComponent(doc.id)}/shares/${encodeURIComponent(share.id)}`,
      {
        headers: this.authHeaders(),
        timeout: 15000
      }
    ).subscribe({
      next: result => {
        this.shareMessage =
          result.message || 'Document access revoked.';
        this.loadShares(doc);
        this.cdr.markForCheck();
      },
      error: (error: HttpErrorResponse) => {
        this.shareError = error.error?.message ??
          'Could not revoke access. Please try again.';
        this.cdr.markForCheck();
      }
    });
  }

  closeShareDialog(): void {
    this.showShareDialog = false;
    this.shareTargetDocument = null;
    this.shareEmail = '';
    this.sharePermission = 'Read';
    this.shareError = '';
    this.shareMessage = '';
    this.existingShares = [];
    this.isSharing = false;
    this.isLoadingShares = false;
    this.cdr.markForCheck();
  }

  // FILE SELECTION
  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;

    this.fileInput = input;

    const file = input.files?.[0] ?? null;

    this.selectedFile = null;
    this.uploadError = '';
    this.uploadMessage = '';

    if (!file) {
      this.cdr.markForCheck();
      return;
    }

    const extension = file.name
      .slice(file.name.lastIndexOf('.'))
      .toLowerCase();

    const allowed = ['.pdf', '.doc', '.docx', '.xls', '.xlsx', '.txt'];

    if (!allowed.includes(extension)) {
      this.uploadError =
        'Choose a PDF, Word, Excel or TXT file.';
      input.value = '';
      this.cdr.markForCheck();
      return;
    }

    if (file.size === 0 || file.size > 10 * 1024 * 1024) {
      this.uploadError = file.size === 0
        ? 'The selected file is empty.'
        : 'Maximum file size is 10 MB.';

      input.value = '';
      this.cdr.markForCheck();
      return;
    }

    this.selectedFile = file;
    this.cdr.markForCheck();
  }

  // UPLOAD DOCUMENT
  uploadDocument(): void {
    if (this.isUploading || !this.selectedFile) {
      this.uploadError = 'Choose a file before uploading.';
      this.cdr.markForCheck();
      return;
    }

    if (!this.accessToken) {
      this.uploadError =
        'Please sign in again before uploading.';
      this.cdr.markForCheck();
      return;
    }

    this.isUploading = true;
    this.uploadError = '';
    this.uploadMessage = '';

    const formData = new FormData();
    formData.append('file', this.selectedFile, this.selectedFile.name);

    this.http.post<{ message?: string }>(
      `${this.apiUrl}/api/documents`,
      formData,
      {
        headers: this.authHeaders(),
        timeout: 30000
      }
    ).subscribe({
      next: result => {
        this.isUploading = false;
        this.uploadMessage =
          result.message ?? 'Document uploaded successfully.';

        this.selectedFile = null;

        if (this.fileInput) {
          this.fileInput.value = '';
        }

        this.cdr.markForCheck();
        this.loadDocuments();
      },
      error: (error: HttpErrorResponse) => {
        this.isUploading = false;

        if (error.status === 401) {
          this.uploadError =
            'Your session expired. Please sign in again.';
        } else if (error.status === 403) {
          this.uploadError =
            'Your account does not have permission to upload.';
        } else if (error.status === 0) {
          this.uploadError = 'Cannot connect to the backend.';
        } else {
          this.uploadError = error.error?.message ??
            'Upload failed. Please try again.';
        }

        this.cdr.markForCheck();
      }
    });
  }

  // MOVE DOCUMENT TO RECYCLE BIN
  deleteDocument(doc: DocumentRow): void {
    if (!this.accessToken || !this.isDocumentOwner(doc)) {
      this.documentError =
        'Only the document owner can move this file to the Recycle Bin.';
      this.cdr.markForCheck();
      return;
    }

    this.documentError = '';
    this.clearDocumentMessageTimer();
    this.documentMessage = '';

    this.http.delete<{ message?: string }>(
      `${this.apiUrl}/api/documents/${encodeURIComponent(doc.id)}`,
      {
        headers: this.authHeaders(),
        timeout: 15000
      }
    ).subscribe({
      next: result => {
        this.documents = this.documents.filter(
          item => item.id !== doc.id
        );

        this.showDocumentSuccessMessage(
          result?.message || 'Document moved to the Recycle Bin.'
        );

        this.loadTrash();
        this.cdr.markForCheck();
      },
      error: (error: HttpErrorResponse) => {
        this.documentError = error.error?.message ||
          'Could not move this document to the Recycle Bin.';
        this.cdr.markForCheck();
      }
    });
  }

  // DOWNLOAD DOCUMENT
  downloadDocument(doc: DocumentRow): void {
    if (!this.accessToken) {
      this.documentError = 'Please sign in again.';
      this.cdr.markForCheck();
      return;
    }

    this.documentError = '';

    this.http.get(
      `${this.apiUrl}/api/documents/${encodeURIComponent(doc.id)}/download`,
      {
        headers: this.authHeaders(),
        responseType: 'blob',
        timeout: 30000
      }
    ).subscribe({
      next: fileBlob => {
        const objectUrl = URL.createObjectURL(fileBlob);
        const link = document.createElement('a');

        link.href = objectUrl;
        link.download = doc.name;
        link.style.display = 'none';

        document.body.appendChild(link);
        link.click();
        link.remove();

        window.setTimeout(
          () => URL.revokeObjectURL(objectUrl),
          1000
        );
      },
      error: (error: HttpErrorResponse) => {
        if (error.status === 401) {
          this.documentError =
            'Your session expired. Please sign in again.';
        } else if (error.status === 403) {
          this.documentError =
            'You do not have permission to download this document.';
        } else {
          this.documentError =
            'Download failed or the file is unavailable.';
        }

        this.cdr.markForCheck();
      }
    });
  }

  // HELPERS
  private getDocumentType(fileName: string): DocumentRow['type'] {
    const extension = fileName
      .slice(fileName.lastIndexOf('.'))
      .toLowerCase();

    if (extension === '.pdf') return 'PDF';
    if (['.doc', '.docx'].includes(extension)) return 'DOC';
    if (['.xls', '.xlsx'].includes(extension)) return 'XLS';

    return 'TXT';
  }

  formatFileSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;

    if (bytes < 1024 * 1024) {
      return `${(bytes / 1024).toFixed(1)} KB`;
    }

    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  private formatDate(value?: string): string {
    if (!value) return 'Not available';

    const date = new Date(value);

    if (Number.isNaN(date.getTime())) return 'Not available';

    return date.toLocaleDateString();
  }

  formatDateTime(value?: string | null): string {
    if (!value) return 'Not available';

    const date = new Date(value);

    if (Number.isNaN(date.getTime())) return 'Not available';

    return date.toLocaleString();
  }

  // LOGOUT
  logout(): void {
    this.closeShareDialog();
    this.closeDocumentDetails();

    this.clearDocumentMessageTimer();
    this.clearTrashMessageTimer();

    this.isLoggedIn = false;
    this.isSubmitting = false;
    this.isLoadingDocuments = false;
    this.isUploading = false;
    this.isLoadingPermissions = false;
    this.isLoadingAudit = false;
    this.isLoadingTrash = false;

    this.email = '';
    this.password = '';
    this.authView = 'login';
    this.isRegisterMode = false;

    this.resetEmail = '';
    this.resetOtp = '';
    this.newPassword = '';
    this.confirmNewPassword = '';
    this.isSendingOtp = false;
    this.isResettingPassword = false;
    this.resetError = '';
    this.resetMessage = '';

    this.searchTerm = '';
    this.accessToken = '';
    this.userRole = '';

    this.loginError = '';
    this.documentError = '';
    this.documentMessage = '';
    this.uploadError = '';
    this.uploadMessage = '';
    this.permissionsError = '';
    this.permissionMessage = '';
    this.auditError = '';

    this.selectedFile = null;
    this.documents = [];
    this.trashedDocuments = [];
    this.trashError = '';
    this.trashMessage = '';
    this.restoringDocumentId = '';
    this.permanentlyDeletingDocumentId = '';

    this.permissions = [];
    this.auditEvents = [];

    this.activeSection = 'documents';
    this.activePage = 'dashboard';

    if (this.fileInput) {
      this.fileInput.value = '';
      this.fileInput = null;
    }

    this.cdr.markForCheck();
  }
}