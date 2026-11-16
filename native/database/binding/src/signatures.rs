// Generated from exact pinned signatures-v1.json; do not edit.
use nativeweave_interop_abi::{NwBytesViewV1,NwErrorInfoV1,NwOperationCallbacksV1,NwStatus};
/// Engine-private owner, never a shared layout.
#[repr(C)] pub struct DbOwner { _private:[u8;0] }
/// Engine-private immutable buffer, distinct from ordinary owners.
#[repr(C)] pub struct DbBuffer { _private:[u8;0] }
pub type NwDbOperationStartV1 = unsafe extern "C" fn(*mut DbOwner, *const NwOperationCallbacksV1, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbOperationRequestCancelV1 = unsafe extern "C" fn(*mut DbOwner) -> NwStatus;
pub type NwDbOperationTakeOwnerV1 = unsafe extern "C" fn(*mut DbOwner, u32, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbOperationTakeBufferV1 = unsafe extern "C" fn(*mut DbOwner, *mut *mut DbBuffer, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbOperationTakeCountV1 = unsafe extern "C" fn(*mut DbOwner, *mut i64, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbOperationTakeErrorV1 = unsafe extern "C" fn(*mut DbOwner, *mut *mut DbBuffer, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbOperationTryRetireV1 = unsafe extern "C" fn(*mut DbOwner) -> NwStatus;
pub type NwDbOperationReleaseV1 = unsafe extern "C" fn(*mut DbOwner) -> ();
pub type NwDbSourceCreateV1 = unsafe extern "C" fn(NwBytesViewV1, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbSessionOpenCreateV1 = unsafe extern "C" fn(*mut DbOwner, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbCodecRegisterV1 = unsafe extern "C" fn(*mut DbOwner, u32, NwBytesViewV1, *mut u32, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbMetadataRegisterV1 = unsafe extern "C" fn(*mut DbOwner, NwBytesViewV1, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbProjectionRegisterV1 = unsafe extern "C" fn(*mut DbOwner, NwBytesViewV1, *mut u32, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbRelationRegisterV1 = unsafe extern "C" fn(*mut DbOwner, *mut DbOwner, *mut DbOwner, NwBytesViewV1, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbRelationLoadCreateV1 = unsafe extern "C" fn(*mut DbOwner, *mut DbOwner, NwBytesViewV1, u32, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbQueryCreateV1 = unsafe extern "C" fn(*mut DbOwner, *mut DbOwner, u32, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbExprColumnV1 = unsafe extern "C" fn(*mut DbOwner, u64, u32, *mut u64, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbExprValueV1 = unsafe extern "C" fn(*mut DbOwner, NwBytesViewV1, *mut u64, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbExprParameterV1 = unsafe extern "C" fn(*mut DbOwner, u32, NwBytesViewV1, u32, u32, *mut u64, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbExprBinaryV1 = unsafe extern "C" fn(*mut DbOwner, u32, u64, u64, *mut u64, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbExprUnaryV1 = unsafe extern "C" fn(*mut DbOwner, u32, u64, *mut u64, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbExprListV1 = unsafe extern "C" fn(*mut DbOwner, u32, u64, NwBytesViewV1, *mut u64, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbQueryImportV1 = unsafe extern "C" fn(*mut DbOwner, *mut DbOwner, u32, *mut u64, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbQueryClauseV1 = unsafe extern "C" fn(*mut DbOwner, u32, NwBytesViewV1, *mut u64, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbQuerySealV1 = unsafe extern "C" fn(*mut DbOwner, u32, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbQueryCloneV1 = unsafe extern "C" fn(*mut DbOwner, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbQueryRenderV1 = unsafe extern "C" fn(*mut DbOwner, *mut *mut DbBuffer, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbCteCreateV1 = unsafe extern "C" fn(*mut DbOwner, u32, *mut DbOwner, u32, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbExecuteCreateV1 = unsafe extern "C" fn(*mut DbOwner, *mut DbOwner, NwBytesViewV1, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbSqlCreateV1 = unsafe extern "C" fn(*mut DbOwner, NwBytesViewV1, NwBytesViewV1, NwBytesViewV1, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbCursorFetchCreateV1 = unsafe extern "C" fn(*mut DbOwner, u32, u64, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbTxBeginCreateV1 = unsafe extern "C" fn(*mut DbOwner, NwBytesViewV1, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbTxFinishCreateV1 = unsafe extern "C" fn(*mut DbOwner, u32, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbSavepointCreateV1 = unsafe extern "C" fn(*mut DbOwner, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbSavepointActionCreateV1 = unsafe extern "C" fn(*mut DbOwner, u32, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbMutationCreateV1 = unsafe extern "C" fn(*mut DbOwner, *mut DbOwner, u32, NwBytesViewV1, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbAdminCreateV1 = unsafe extern "C" fn(*mut DbOwner, u32, NwBytesViewV1, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbCloseCreateV1 = unsafe extern "C" fn(*mut DbOwner, *mut *mut DbOwner, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbSnapshotV1 = unsafe extern "C" fn(*mut DbOwner, NwBytesViewV1, *mut *mut DbBuffer, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbOwnerReleaseV1 = unsafe extern "C" fn(*mut DbOwner) -> ();
pub type NwDbBufferViewV1 = unsafe extern "C" fn(*mut DbBuffer, *mut NwBytesViewV1, *mut NwErrorInfoV1) -> NwStatus;
pub type NwDbBufferReleaseV1 = unsafe extern "C" fn(*mut DbBuffer) -> ();
