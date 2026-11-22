// SPDX-License-Identifier: MIT
#pragma once

#include "nw/query.hpp"

namespace nw
{

struct Mutation
{
    RenderedQuery command;
    bool checked = false;
    bool key_bound = false;
    std::size_t input_rows = 1;
    // Called by the Host after execution, before publishing the affected count.
    // Provider errors and batch/transaction progress stay with the Host.
    void validate_affected(std::int64_t rows) const;
};

[[nodiscard]] Mutation compile_mutation(const ModelBinding& model, std::uint32_t kind, ByteView payload,
                                        std::optional<ProjectionBinding> projection, QueryLimits limits,
                                        std::uint64_t timeout_ns);

struct RelationCapabilities
{
    bool metadata = false;
    bool scalar_codecs = false;
    bool query = false;
    bool streaming = false;
    bool explicit_relationships = false;
    void validate() const;
};

struct RelationBinding
{
    SourceIdentity source;
    std::shared_ptr<const Relation> relation;
    ModelBinding parent;
    ModelBinding child;
    std::vector<std::uint32_t> key_codecs;
    RelationCapabilities capabilities;
    [[nodiscard]] static std::shared_ptr<RelationBinding> create(SourceIdentity source, ModelBinding parent,
                                                                 ModelBinding child, ByteView definition,
                                                                 RelationCapabilities capabilities);
};

// The returned command owns copied key cells and immutable model/projection bindings.
// No provider or connection is touched during admission.
[[nodiscard]] RenderedQuery compile_relation(const RelationBinding& relation, SourceIdentity session_source,
                                             ByteView key, ProjectionBinding projection, QueryLimits limits,
                                             std::uint64_t timeout_ns);

} // namespace nw
