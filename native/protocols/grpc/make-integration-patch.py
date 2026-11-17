"""Write the exact reviewed shared-host seam; the batch owner applies serially."""
from pathlib import Path
import difflib
root=Path(__file__).resolve().parents[3]
changes={}
def edit(path):
    p=root/path; original=p.read_text();changes[path]=[original,original];return original
def replace(path,old,new,count=1):
    if path not in changes:edit(path)
    source=changes[path][1]
    if source.count(old)<count:raise ValueError((path,old))
    changes[path][1]=source.replace(old,new,count)
lib='native/server/host/src/lib.rs'
replace(lib,'    code: i32,','    code: i32,\n    error_domain: u32,')
replace(lib,'Self { status, code: 0 }','Self { status, code: 0, error_domain: 0x53525652 }')
replace(lib,'            code,','            code,\n            error_domain: 0x53525652,')
replace(lib,'domain: 0x53525652,\n            code: self.code','domain: self.error_domain,\n            code: self.code')
config='native/server/host/src/config.rs'
replace(config,'    pub mcp: Option<Arc<nativeweave_protocol_mcp::profile::Profile>>,','    pub mcp: Option<Arc<nativeweave_protocol_mcp::profile::Profile>>,\n    pub grpc: Option<Arc<nativeweave_protocols_grpc::Descriptor>>,')
replace(config,'if v.kind != 0 && v.kind != 2 {','if v.kind > 2 {')
replace(config,'    let mcp = if v.kind == 2 {','''    let grpc = if v.kind == 1 {
        if !parameters.is_empty() || parsed != [Method::POST] || v.max_json_bytes != 0 { return Err(invalid()); }
        // SAFETY: the bounded exact-schema descriptor is consumed during synchronous registration.
        let descriptor = nativeweave_protocols_grpc::Descriptor::parse(
            unsafe { bytes(v.protocol_descriptor, 16_384)? }, &pattern, v.handler_id,
            duration(v.timeout_ns, false)?, v.max_request_bytes).map_err(|_| invalid())?;
        Some(Arc::new(descriptor))
    } else { None };
    let mcp = if v.kind == 2 {''')
replace(config,'        mcp,\n','        mcp,\n        grpc,\n')
meta='native/server/host/src/metadata.rs'
replace(meta,'    pub headers: Vec<u8>,','    pub headers: Vec<u8>,\n    pub grpc: Option<nativeweave_protocols_grpc::RequestMetadata>,')
replace(meta,'if content_length.is_some_and(|n| n > route.max_request_bytes) {','if route.kind != 1 && content_length.is_some_and(|n| n > route.max_request_bytes) {')
replace(meta,'        headers: config::encode_headers(request.headers())?,','        headers: config::encode_headers(request.headers())?,\n        grpc: None,')
engine='native/server/host/src/engine.rs'
replace(engine,'pub mod mcp;','pub mod mcp;\n#[path = "../../../protocols/grpc/host.rs"]\npub mod grpc;')
replace(engine,'        Ok(router)\n','        Ok(router.fallback(grpc::fallback))\n')
replace(engine,'        let deadline = Instant::now() + route.timeout;','        if route.kind == 1 { return grpc::dispatch(self, route.clone(), parameters, request).await; }\n        let deadline = Instant::now() + route.timeout;')
replace(engine,'            mcp,\n','            mcp,\n            grpc: None,\n')
replace(engine,'    pub mcp: Option<mcp::State>,','    pub mcp: Option<mcp::State>,\n    pub grpc: Option<Arc<nativeweave_protocols_grpc::Call>>,')
replace(engine,'        if let Some(mcp) = &self.mcp {','        if let Some(call) = &self.grpc { call.stop_input(); }\n        grpc::release_metadata_if_done(self);\n        if let Some(mcp) = &self.mcp {')
replace(engine,'        self.body_done.store(true, Ordering::Release);','        self.body_done.store(true, Ordering::Release);\n        grpc::release_metadata_if_done(self);')
replace(engine,'    pub fn discard_input(self: &Arc<Self>) {','    pub fn discard_input(self: &Arc<Self>) {\n        if self.grpc.is_some() { grpc::discard(self); return; }')
replace(engine,'            && self.budget.snapshot() == (0, 0)','            && self.budget.snapshot() == (grpc::remaining_anchor(self), 0)\n            && grpc::metadata_released(self)')
replace(engine,'    pub reservation: WriteReservation,','    pub reservation: grpc::WriteToken,')
replace(engine,'    Data(OwnedBuffer),','    Data(OwnedBuffer),\n    GrpcData(nativeweave_protocols_grpc::MessageBuffer),')
replace(engine,'pub enum Job {','''pub enum Job {
    GrpcRead(Arc<RequestState>, SideGuard),
    GrpcReserve(Arc<RequestState>, usize, SideGuard),
    GrpcWrite(Arc<RequestState>, nativeweave_protocols_grpc::Prepared, SideGuard),
    GrpcHeaders(Arc<RequestState>, nativeweave_protocols_grpc::ResponseMetadata, SideGuard),
    GrpcStatus(Arc<RequestState>, nativeweave_protocols_grpc::tonic::Status, nativeweave_protocols_grpc::ResponseMetadata, SideGuard),''')
replace(engine,'            Job::Read(..) => 2,','            Job::Read(..) | Job::GrpcRead(..) => 2,')
replace(engine,'            Job::Reserve(..) => 3,','            Job::Reserve(..) | Job::GrpcReserve(..) => 3,')
replace(engine,'            match job {\n','''            match job {
                Job::GrpcRead(request, side) => grpc::read(request, side).await,
                Job::GrpcReserve(request, capacity, side) => grpc::reserve(request, capacity, side).await,
                Job::GrpcWrite(request, prepared, side) => grpc::write(request, prepared, side),
                Job::GrpcHeaders(request, metadata, side) => grpc::headers(request, metadata, side),
                Job::GrpcStatus(request, status, metadata, side) => grpc::status(request, status, metadata, side).await,
''')
replace(engine,'                        reservation,\n                        side,','                        reservation: grpc::WriteToken::Body(reservation),\n                        side,')
exports='native/server/host/src/exports.rs'
replace(exports,'static BOUNDARY: EngineBoundary','#[path = "../../../protocols/grpc/exports.rs"]\nmod grpc;\n\nstatic BOUNDARY: EngineBoundary')
replace(exports,'    buffer: Option<OwnedBuffer>,','    buffer: Option<crate::engine::grpc::Buffer>,')
replace(exports,'use nativeweave_server_streaming::{Credit, OwnedBuffer, Side};','use nativeweave_server_streaming::{Credit, Side};')
replace(exports,'capabilities: 111,','capabilities: 127,')
replace(exports,'    op.inner.cancel.cancel();','    crate::engine::grpc::cancel_read(&op.inner);\n    op.inner.cancel.cancel();')
replace(exports,'        if kind != 0 && kind != 2 && kind != 3 {','''        if kind == 1 {
            // SAFETY: output was initialized above; the gRPC reservation is request-kind checked independently.
            return unsafe { grpc::reserve(request, native_capacity, out_op) };
        }
        if kind != 0 && kind != 2 && kind != 3 {''')
replace(exports,'                buffer.buffer = Some(value);','                buffer.buffer = Some(value.into());')
replace(exports,'            crate::engine::ChunkResult::End(trailers) => {','''            crate::engine::ChunkResult::GrpcData(value) => {
                buffer.buffer = Some(value.into());
                // SAFETY: complete protobuf message, including zero length, transfers as a kind0 owner.
                unsafe { out_frame_kind.write(0); out_buffer.write(Box::into_raw(buffer)); }
            }
            crate::engine::ChunkResult::End(trailers) => {''')
replace(exports,'        if destination.is_null() {','        if buffer.len() == 0 { return Ok(()); }\n        if destination.is_null() {')
replace(exports,'            buffer: Some(buffer),','            buffer: Some(buffer.into()),')
replace(exports,'Job::McpReply(request, reply_kind, value, token.reservation, token.side)','Job::McpReply(request, reply_kind, value, token.reservation.into_body(), token.side)')
# Preserve public signatures exactly; only the implementation body delegates to the owned child.
s=changes[exports][1]
for name,body in [
 ('nw_server_grpc_read_create','unsafe { grpc::read(_request, out_op, error) }'),
 ('nw_server_grpc_write_create','unsafe { grpc::write(_request, _reservation, _message, out_op, error) }'),
 ('nw_server_grpc_headers_create','unsafe { grpc::headers(_request, _metadata, out_op, error) }'),
 ('nw_server_grpc_status_create','unsafe { grpc::status(_request, _grpc_status, _detail, _trailers, out_op, error) }'),
 ('nw_server_metadata_grpc_headers','unsafe { grpc::metadata(_metadata, out_metadata, error) }')]:
 start=s.index('pub unsafe extern "C" fn '+name);a=s.index(') -> u32 {',start)+len(') -> u32 {'); depth=1;b=a
 while depth:
  if s[b]=='{':depth+=1
  elif s[b]=='}':depth-=1
  b+=1
 s=s[:a]+'\n    // SAFETY: the exact borrowed-input/typed-owner ABI contract is forwarded unchanged.\n    '+body+'\n'+s[b-1:]
changes[exports][1]=s
# Existing B17 fixture is only updated for additive private Route fields.
test='native/server/host/src/tests.rs'
replace(test,'        max_json_bytes: 64,\n','        max_json_bytes: 64,\n        mcp: None,\n        grpc: None,\n')
patch=''.join(''.join(difflib.unified_diff(a.splitlines(True),b.splitlines(True),fromfile='a/'+p,tofile='b/'+p)) for p,(a,b) in changes.items())
output=root/'native/protocols/grpc/integration.patch';output.write_text(patch);print(output)
