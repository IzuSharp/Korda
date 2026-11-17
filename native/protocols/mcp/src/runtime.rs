//! The public pinned rmcp ServerHandler and StreamableHttpService own MCP/JSON-RPC.
use crate::profile::{Profile, conforms};
use bytes::Bytes;
use http::{Request, Response};
use http_body_util::{Full, combinators::BoxBody};
use rmcp::{
    ErrorData, RoleServer, ServerHandler,
    model::*,
    service::RequestContext,
    transport::streamable_http_server::{
        StreamableHttpServerConfig, StreamableHttpService, session::local::LocalSessionManager,
    },
};
use serde_json::Value;
use std::{borrow::Cow, convert::Infallible, future::Future, pin::Pin, sync::Arc};
use tokio_util::sync::CancellationToken;
use tower::ServiceExt;

/// One fixed-kind, registered-handler continuation on the existing host ticket.
/// The host owns admission/retirement and the cross-language argument/result owner.
pub trait ForeignCall: Send + Sync + 'static {
    fn invoke(
        &self,
        kind: u32,
        handler: u64,
        arguments: Value,
    ) -> Pin<Box<dyn Future<Output = Result<Value, ErrorData>> + Send + '_>>;
}
pub struct ForeignMcp {
    pub profile: Arc<Profile>,
    pub call: Arc<dyn ForeignCall>,
}
fn invalid() -> ErrorData {
    ErrorData::invalid_params("Invalid statically registered arguments", None)
}
impl ServerHandler for ForeignMcp {
    fn supported_protocol_versions(&self) -> Cow<'static, [ProtocolVersion]> {
        Cow::Owned(vec![ProtocolVersion::V_2026_07_28])
    }
    fn get_info(&self) -> ServerConfig {
        let mut caps = ServerCapabilities::default();
        if !self.profile.tool_models.is_empty() {
            caps.tools = Some(Default::default());
        }
        if !self.profile.resource_models.is_empty() {
            caps.resources = Some(Default::default());
        }
        if !self.profile.prompt_models.is_empty() {
            caps.prompts = Some(Default::default());
        }
        ServerConfig::new(caps)
    }
    fn get_tool(&self, name: &str) -> Option<Tool> {
        self.profile
            .tool_models
            .iter()
            .find(|t| t.name == name)
            .cloned()
    }
    async fn list_tools(
        &self,
        request: Option<PaginatedRequestParams>,
        _: RequestContext<RoleServer>,
    ) -> Result<ListToolsResult, ErrorData> {
        if request.is_some_and(|r| r.cursor.is_some()) {
            return Err(invalid());
        }
        Ok(ListToolsResult::with_all_items(
            self.profile.tool_models.clone(),
        ))
    }
    async fn list_resources(
        &self,
        request: Option<PaginatedRequestParams>,
        _: RequestContext<RoleServer>,
    ) -> Result<ListResourcesResult, ErrorData> {
        if request.is_some_and(|r| r.cursor.is_some()) {
            return Err(invalid());
        }
        Ok(ListResourcesResult::with_all_items(
            self.profile.resource_models.clone(),
        ))
    }
    async fn list_prompts(
        &self,
        request: Option<PaginatedRequestParams>,
        _: RequestContext<RoleServer>,
    ) -> Result<ListPromptsResult, ErrorData> {
        if request.is_some_and(|r| r.cursor.is_some()) {
            return Err(invalid());
        }
        Ok(ListPromptsResult::with_all_items(
            self.profile.prompt_models.clone(),
        ))
    }
    async fn call_tool(
        &self,
        request: CallToolRequestParams,
        _: RequestContext<RoleServer>,
    ) -> Result<CallToolResponse, ErrorData> {
        let tool = self
            .profile
            .descriptor
            .tools
            .iter()
            .find(|t| t.name == request.name)
            .ok_or_else(invalid)?;
        let args = Value::Object(request.arguments.ok_or_else(invalid)?);
        if !conforms(&args, &tool.input_schema) {
            return Err(invalid());
        }
        let value = self.call.invoke(0, tool.handler_id, args).await?;
        let model: CallToolResult = serde_json::from_value(value)
            .map_err(|_| ErrorData::internal_error("Invalid application output", None))?;
        Ok(model.into())
    }
    async fn read_resource(
        &self,
        request: ReadResourceRequestParams,
        _: RequestContext<RoleServer>,
    ) -> Result<ReadResourceResponse, ErrorData> {
        let resource = self
            .profile
            .descriptor
            .resources
            .iter()
            .find(|t| t.uri == request.uri)
            .ok_or_else(invalid)?;
        let value = self
            .call
            .invoke(
                1,
                resource.handler_id,
                serde_json::json!({"uri":resource.uri}),
            )
            .await?;
        let model: ReadResourceResult = serde_json::from_value(value)
            .map_err(|_| ErrorData::internal_error("Invalid application output", None))?;
        Ok(model.into())
    }
    async fn get_prompt(
        &self,
        request: GetPromptRequestParams,
        _: RequestContext<RoleServer>,
    ) -> Result<GetPromptResponse, ErrorData> {
        let prompt = self
            .profile
            .descriptor
            .prompts
            .iter()
            .find(|t| t.name == request.name)
            .ok_or_else(invalid)?;
        let arguments = request.arguments.unwrap_or_default();
        if arguments.iter().any(|(key, value)| {
            !value.is_string() || !prompt.arguments.iter().any(|a| a.name == *key)
        }) || prompt
            .arguments
            .iter()
            .any(|a| a.required && !arguments.contains_key(&a.name))
        {
            return Err(invalid());
        }
        let value = self
            .call
            .invoke(2, prompt.handler_id, Value::Object(arguments))
            .await?;
        let model: GetPromptResult = serde_json::from_value(value)
            .map_err(|_| ErrorData::internal_error("Invalid application output", None))?;
        Ok(model.into())
    }
}

pub async fn serve(
    profile: Arc<Profile>,
    hosts: Vec<String>,
    request: Request<Full<Bytes>>,
    call: Arc<dyn ForeignCall>,
    cancel: CancellationToken,
) -> Response<BoxBody<Bytes, Infallible>> {
    let config = StreamableHttpServerConfig::default()
        .with_legacy_session_mode(false)
        .with_json_response(true)
        .with_stateless_protocol_metadata_required(true)
        .with_max_request_body_bytes(profile.limits.wire)
        .with_allowed_hosts(hosts)
        .with_allowed_origins(profile.descriptor.allowed_origins.clone())
        .enforce_origin_validation()
        .with_cancellation_token(cancel);
    let service: StreamableHttpService<ForeignMcp, LocalSessionManager> =
        StreamableHttpService::new(
            move || {
                Ok(ForeignMcp {
                    profile: profile.clone(),
                    call: call.clone(),
                })
            },
            Arc::new(LocalSessionManager::default()),
            config,
        );
    match service.oneshot(request).await {
        Ok(response) => response,
        Err(never) => match never {},
    }
}
