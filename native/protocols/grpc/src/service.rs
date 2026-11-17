//! The four public Tonic service traits, with one immutable per-call dispatch.
use crate::{
    Call, Inbound,
    call::Output,
    codec::{ChunkBody, CodecMemory, CreditedBody, RawCodec},
};
use bytes::Bytes;
use http_body::Body;
use std::{future::Future, pin::Pin, sync::Arc};
use tonic::{
    Status, Streaming,
    server::{
        ClientStreamingService, Grpc, ServerStreamingService, StreamingService, UnaryService,
    },
};
type Reply<T> = Pin<Box<dyn Future<Output = Result<tonic::Response<T>, Status>> + Send>>;
type Dispatch = Arc<dyn Fn() -> Result<(), Status> + Send + Sync>;
struct Unary {
    call: Arc<Call>,
    dispatch: Dispatch,
}
struct Client {
    call: Arc<Call>,
    dispatch: Dispatch,
    memory: Arc<CodecMemory>,
}
struct Server {
    call: Arc<Call>,
    dispatch: Dispatch,
}
struct Duplex {
    call: Arc<Call>,
    dispatch: Dispatch,
    memory: Arc<CodecMemory>,
}
impl UnaryService<Bytes> for Unary {
    type Response = Bytes;
    type Future = Reply<Bytes>;
    fn call(&mut self, request: tonic::Request<Bytes>) -> Self::Future {
        let call = self.call.clone();
        let dispatch = self.dispatch.clone();
        Box::pin(async move {
            call.set_input(Inbound::One(Some(request.into_inner())))
                .await;
            dispatch()?;
            call.unary_response().await
        })
    }
}
impl ClientStreamingService<Bytes> for Client {
    type Response = Bytes;
    type Future = Reply<Bytes>;
    fn call(&mut self, request: tonic::Request<Streaming<Bytes>>) -> Self::Future {
        let call = self.call.clone();
        let dispatch = self.dispatch.clone();
        let memory = self.memory.clone();
        Box::pin(async move {
            call.set_input(Inbound::stream(request.into_inner(), memory))
                .await;
            dispatch()?;
            call.unary_response().await
        })
    }
}
impl ServerStreamingService<Bytes> for Server {
    type Response = Bytes;
    type ResponseStream = Output;
    type Future = Reply<Output>;
    fn call(&mut self, request: tonic::Request<Bytes>) -> Self::Future {
        let call = self.call.clone();
        let dispatch = self.dispatch.clone();
        Box::pin(async move {
            call.set_input(Inbound::One(Some(request.into_inner())))
                .await;
            dispatch()?;
            call.stream_response().await
        })
    }
}
impl StreamingService<Bytes> for Duplex {
    type Response = Bytes;
    type ResponseStream = Output;
    type Future = Reply<Output>;
    fn call(&mut self, request: tonic::Request<Streaming<Bytes>>) -> Self::Future {
        let call = self.call.clone();
        let dispatch = self.dispatch.clone();
        let memory = self.memory.clone();
        Box::pin(async move {
            call.set_input(Inbound::stream(request.into_inner(), memory))
                .await;
            dispatch()?;
            call.stream_response().await
        })
    }
}
pub async fn serve<B>(
    request: http::Request<B>,
    call: Arc<Call>,
    chunk: usize,
    dispatch: Arc<dyn Fn() -> Result<(), Status> + Send + Sync>,
) -> http::Response<tonic::body::Body>
where
    B: Body<Data = Bytes> + Send + Unpin + 'static,
    B::Error: Into<Box<dyn std::error::Error + Send + Sync>> + Send,
{
    let request = request.map(|inner| ChunkBody {
        inner,
        pending: None,
        maximum: chunk,
    });
    let codec = match RawCodec::new(
        call.descriptor.clone(),
        call.budget.clone(),
        chunk,
        call.frames.clone(),
    ) {
        Ok(codec) => codec,
        Err(status) => return status.into_http(),
    };
    let memory = codec.memory.clone();
    let mut grpc = Grpc::new(codec)
        .max_decoding_message_size(call.descriptor.max_encoded_message_bytes as usize)
        .max_encoding_message_size(call.descriptor.max_encoded_message_bytes as usize);
    let response = match call.descriptor.method_kind {
        crate::MethodKind::Unary => {
            grpc.unary(
                Unary {
                    call: call.clone(),
                    dispatch,
                },
                request,
            )
            .await
        }
        crate::MethodKind::ClientStreaming => {
            grpc.client_streaming(
                Client {
                    call: call.clone(),
                    dispatch,
                    memory: memory.clone(),
                },
                request,
            )
            .await
        }
        crate::MethodKind::ServerStreaming => {
            grpc.server_streaming(
                Server {
                    call: call.clone(),
                    dispatch,
                },
                request,
            )
            .await
        }
        crate::MethodKind::Duplex => {
            grpc.streaming(
                Duplex {
                    call: call.clone(),
                    dispatch,
                    memory: memory.clone(),
                },
                request,
            )
            .await
        }
    };
    let (parts, body) = response.into_parts();
    http::Response::from_parts(
        parts,
        tonic::body::Body::new(CreditedBody {
            inner: body,
            frames: call.frames.clone(),
            _memory: memory,
        }),
    )
}
