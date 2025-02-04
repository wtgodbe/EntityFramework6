// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

namespace System.Data.Entity.Core.Common.Utils
{
    using System.Collections.Generic;
    using System.Data.Entity.Core.Mapping;
    using System.Data.Entity.Core.Metadata.Edm;
    using System.Data.Entity.Core.Metadata.Edm.Provider;
    using System.Data.Entity.Resources;
    using System.Data.Entity.Utilities;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Security.Cryptography;

    // Helper functions to get metadata information
    [SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling")]
    internal static class MetadataHelper
    {
        // <summary>
        // Returns an element type of the collection returned by the function import.
        // Returns false, if element type cannot be determined.
        // </summary>
        internal static bool TryGetFunctionImportReturnType<T>(EdmFunction functionImport, int resultSetIndex, out T returnType)
            where T : EdmType
        {
            T resultType;
            if (TryGetWrappedReturnEdmTypeFromFunctionImport(functionImport, resultSetIndex, out resultType))
            {
                if (typeof(EntityType).Equals(typeof(T)) && resultType is EntityType
                    || typeof(ComplexType).Equals(typeof(T)) && resultType is ComplexType
                    || typeof(StructuralType).Equals(typeof(T)) && resultType is StructuralType
                    || typeof(EdmType).Equals(typeof(T)) && resultType is EdmType)
                {
                    returnType = resultType;
                    return true;
                }
            }
            returnType = null;
            return false;
        }

        private static bool TryGetWrappedReturnEdmTypeFromFunctionImport<T>(
            EdmFunction functionImport, int resultSetIndex, out T resultType) where T : EdmType
        {
            resultType = null;

            CollectionType collectionType;
            if (TryGetFunctionImportReturnCollectionType(functionImport, resultSetIndex, out collectionType))
            {
                resultType = collectionType.TypeUsage.EdmType as T;
                return true;
            }
            return false;
        }

        // <summary>
        // effects: determines if the given function import returns collection type, and if so returns the type
        // </summary>
        internal static bool TryGetFunctionImportReturnCollectionType(
            EdmFunction functionImport, int resultSetIndex, out CollectionType collectionType)
        {
            var returnParameter = GetReturnParameter(functionImport, resultSetIndex);
            if (returnParameter != null
                && returnParameter.TypeUsage.EdmType.BuiltInTypeKind == BuiltInTypeKind.CollectionType)
            {
                collectionType = (CollectionType)returnParameter.TypeUsage.EdmType;
                return true;
            }
            collectionType = null;
            return false;
        }

        // <summary>
        // Gets the resultSetIndexth return parameter for functionImport, or null if resultSetIndex is out of range
        // </summary>
        internal static FunctionParameter GetReturnParameter(EdmFunction functionImport, int resultSetIndex)
        {
            return functionImport.ReturnParameters.Count > resultSetIndex
                       ? functionImport.ReturnParameters[resultSetIndex]
                       : null;
        }

        internal static EdmFunction GetFunctionImport(
            string functionName, string defaultContainerName, MetadataWorkspace workspace,
            out string containerName, out string functionImportName)
        {
            // find FunctionImport

            CommandHelper.ParseFunctionImportCommandText(
                functionName, defaultContainerName,
                out containerName, out functionImportName);
            return CommandHelper.FindFunctionImport(workspace, containerName, functionImportName);
        }

        // <summary>
        // Gets the resultSetIndexth result edm type, and ensure that it is consistent with EntityType.
        // </summary>
        internal static EdmType GetAndCheckFunctionImportReturnType<TElement>(
            EdmFunction functionImport, int resultSetIndex, MetadataWorkspace workspace)
        {
            EdmType expectedEdmType;
            if (!TryGetFunctionImportReturnType(functionImport, resultSetIndex, out expectedEdmType))
            {
                throw EntityUtil.ExecuteFunctionCalledWithNonReaderFunction(functionImport);
            }
            CheckFunctionImportReturnType<TElement>(expectedEdmType, workspace);

            return expectedEdmType;
        }

        // <summary>
        // check that the type TElement and function metadata are consistent
        // </summary>
        internal static void CheckFunctionImportReturnType<TElement>(EdmType expectedEdmType, MetadataWorkspace workspace)
        {
            // currently there are only two possible spatial O-space types, but 16 C-space types.   
            // Normalize the C-space type to the base type before we check to see if it matches the O-space type.
            bool isGeographic;
            var spatialNormalizedEdmType = expectedEdmType;
            if (Helper.IsSpatialType(expectedEdmType, out isGeographic))
            {
                spatialNormalizedEdmType =
                    PrimitiveType.GetEdmPrimitiveType(isGeographic ? PrimitiveTypeKind.Geography : PrimitiveTypeKind.Geometry);
            }

            EdmType modelEdmType;
            if (!workspace.TryDetermineCSpaceModelType<TElement>(out modelEdmType)
                || !modelEdmType.EdmEquals(spatialNormalizedEdmType))
            {
                throw new InvalidOperationException(
                    Strings.ObjectContext_ExecuteFunctionTypeMismatch(
                        typeof(TElement).FullName,
                        expectedEdmType.FullName));
            }
        }

        // Returns ParameterDirection corresponding to given ParameterMode
        internal static ParameterDirection ParameterModeToParameterDirection(ParameterMode mode)
        {
            switch (mode)
            {
                case ParameterMode.In:
                    return ParameterDirection.Input;

                case ParameterMode.InOut:
                    return ParameterDirection.InputOutput;

                case ParameterMode.Out:
                    return ParameterDirection.Output;

                case ParameterMode.ReturnValue:
                    return ParameterDirection.ReturnValue;

                default:
                    Debug.Fail("unrecognized mode " + mode.ToString());
                    return default(ParameterDirection);
            }
        }

        // effects: Returns true iff member is present in type.Members
        internal static bool DoesMemberExist(StructuralType type, EdmMember member)
        {
            foreach (var child in type.Members)
            {
                if (child.Equals(member))
                {
                    return true;
                }
            }
            return false;
        }

        // <summary>
        // Returns true iff member's is a simple non-structures scalar such as primitive or enum.
        // </summary>
        internal static bool IsNonRefSimpleMember(EdmMember member)
        {
            return member.TypeUsage.EdmType.BuiltInTypeKind == BuiltInTypeKind.PrimitiveType ||
                   member.TypeUsage.EdmType.BuiltInTypeKind == BuiltInTypeKind.EnumType;
        }

        // effects: Returns true if member's type has a discrete domain (i.e. is bolean type)
        // Note: enums don't have discrete domains as we allow domain of the underlying type.
        internal static bool HasDiscreteDomain(EdmType edmType)
        {
            var primitiveType = edmType as PrimitiveType;

            return primitiveType != null && primitiveType.PrimitiveTypeKind == PrimitiveTypeKind.Boolean;
        }

        // requires: end is given
        // effects: determine the entity type for an association end member
        internal static EntityType GetEntityTypeForEnd(AssociationEndMember end)
        {
            DebugCheck.NotNull(end);
            Debug.Assert(
                end.TypeUsage.EdmType.BuiltInTypeKind == BuiltInTypeKind.RefType,
                "type of association end member must be ref");
            var refType = (RefType)end.TypeUsage.EdmType;
            var endType = refType.ElementType;
            Debug.Assert(
                endType.BuiltInTypeKind == BuiltInTypeKind.EntityType,
                "type of association end reference element must be entity type");
            return (EntityType)endType;
        }

        // effects: Returns the entity set at the end corresponding to endMember
        internal static EntitySet GetEntitySetAtEnd(
            AssociationSet associationSet,
            AssociationEndMember endMember)
        {
            return associationSet.AssociationSetEnds[endMember.Name].EntitySet;
        }

        // effects: Returns the AssociationEndMember at the other end of the parent association (first found)
        internal static AssociationEndMember GetOtherAssociationEnd(AssociationEndMember endMember)
        {
            var members = endMember.DeclaringType.Members;
            Debug.Assert(members.Count == 2, "only expecting two end members");

            var otherMember = members[0];
            if (!ReferenceEquals(endMember, otherMember))
            {
                Debug.Assert(ReferenceEquals(endMember, members[1]), "didn't match other member");
                return (AssociationEndMember)otherMember;
            }
            return (AssociationEndMember)members[1];
        }

        // effects: Returns true iff every end other than "endProperty" has a lower
        // multiplicity of at least one
        internal static bool IsEveryOtherEndAtLeastOne(
            AssociationSet associationSet,
            AssociationEndMember member)
        {
            foreach (var end in associationSet.AssociationSetEnds)
            {
                var endMember = end.CorrespondingAssociationEndMember;
                if (endMember.Equals(member) == false
                    &&
                    GetLowerBoundOfMultiplicity(endMember.RelationshipMultiplicity) == 0)
                {
                    return false;
                }
            }
            return true;
        }

        // requires: toEnd and type are given
        // effects: determines whether the given association end can be referenced by an entity of the given type
        internal static bool IsAssociationValidForEntityType(AssociationSetEnd toEnd, EntityType type)
        {
            DebugCheck.NotNull(toEnd);
            DebugCheck.NotNull(type);

            // get the opposite end which includes the relevant type information
            var fromEnd = GetOppositeEnd(toEnd);
            var fromType = GetEntityTypeForEnd(fromEnd.CorrespondingAssociationEndMember);
            return (fromType.IsAssignableFrom(type));
        }

        // requires: end is given
        // effects: returns the opposite end in the association
        internal static AssociationSetEnd GetOppositeEnd(AssociationSetEnd end)
        {
            DebugCheck.NotNull(end);
            // there must be exactly one ("Single") other end that isn't ("Filter") this end
            var otherEnd = end.ParentAssociationSet.AssociationSetEnds.Where(
                e => !e.EdmEquals(end)).Single();
            return otherEnd;
        }

        // requires: function is not null
        // effects: Returns true if the given function is composable.
        internal static bool IsComposable(EdmFunction function)
        {
            DebugCheck.NotNull(function);
            MetadataProperty isComposableProperty;
            if (function.MetadataProperties.TryGetValue("IsComposableAttribute", false, out isComposableProperty))
            {
                return (bool)isComposableProperty.Value;
            }
            else
            {
                return !function.IsFunctionImport;
            }
        }

        // requires: member is EdmProperty or AssociationEndMember
        // effects: Returns true if member is nullable
        internal static bool IsMemberNullable(EdmMember member)
        {
            DebugCheck.NotNull(member);
            Debug.Assert(Helper.IsEdmProperty(member) || Helper.IsAssociationEndMember(member));
            if (Helper.IsEdmProperty(member))
            {
                return ((EdmProperty)member).Nullable;
            }
            return false;
        }

        // <summary>
        // Given a table EntitySet this function finds out all C-side EntitySets that are mapped to the table.
        // </summary>
        internal static IEnumerable<EntitySet> GetInfluencingEntitySetsForTable(EntitySet table, MetadataWorkspace workspace)
        {
            Debug.Assert(table.EntityContainer.GetDataSpace() == DataSpace.SSpace);

            ItemCollection itemCollection = null;
            workspace.TryGetItemCollection(DataSpace.CSSpace, out itemCollection);
            var containerMapping = MappingMetadataHelper.GetEntityContainerMap(
                (StorageMappingItemCollection)itemCollection, table.EntityContainer);

            //find EntitySetMappings where one of the mapping fragment maps some type to the given table
            return containerMapping.EntitySetMaps
                                   .Where(
                                       map => map.TypeMappings.Any(
                                           typeMap => typeMap.MappingFragments.Any(
                                               mappingFrag => mappingFrag.TableSet.EdmEquals(table)
                                                          )
                                                  )
                )
                                   .Select(m => m.Set)
                                   .Cast<EntitySet>()
                                   .Distinct();
        }

        // effects: Returns this type and its sub types - for refs, gets the
        // type and subtypes of the entity type
        internal static IEnumerable<EdmType> GetTypeAndSubtypesOf(EdmType type, MetadataWorkspace workspace, bool includeAbstractTypes)
        {
            return GetTypeAndSubtypesOf(type, workspace.GetItemCollection(DataSpace.CSpace), includeAbstractTypes);
        }

        internal static IEnumerable<EdmType> GetTypeAndSubtypesOf(EdmType type, ItemCollection itemCollection, bool includeAbstractTypes)
        {
            // We have to collect subtypes in ref to support conditional association mappings
            if (Helper.IsRefType(type))
            {
                type = ((RefType)type).ElementType;
            }

            if (includeAbstractTypes || !type.Abstract)
            {
                yield return type;
            }

            // Get entity sub-types
            foreach (var subType in GetTypeAndSubtypesOf<EntityType>(type, itemCollection, includeAbstractTypes))
            {
                yield return subType;
            }

            // Get complex sub-types
            foreach (var subType in GetTypeAndSubtypesOf<ComplexType>(type, itemCollection, includeAbstractTypes))
            {
                yield return subType;
            }
        }

        private static IEnumerable<EdmType> GetTypeAndSubtypesOf<T_EdmType>(
            EdmType type, ItemCollection itemCollection, bool includeAbstractTypes)
            where T_EdmType : EdmType
        {
            // Get the subtypes of the type from the WorkSpace
            var specificType = type as T_EdmType;
            if (specificType != null)
            {
                IEnumerable<T_EdmType> typesInWorkSpace = itemCollection.GetItems<T_EdmType>();
                foreach (var typeInWorkSpace in typesInWorkSpace)
                {
                    if (specificType.Equals(typeInWorkSpace) == false
                        && Helper.IsSubtypeOf(typeInWorkSpace, specificType))
                    {
                        if (includeAbstractTypes || !typeInWorkSpace.Abstract)
                        {
                            yield return typeInWorkSpace;
                        }
                    }
                }
            }
            yield break;
        }

        internal static IEnumerable<EdmType> GetTypeAndParentTypesOf(EdmType type, bool includeAbstractTypes)
        {
            // We have to collect subtypes in ref to support conditional association mappings
            if (Helper.IsRefType(type))
            {
                type = ((RefType)type).ElementType;
            }

            var specificType = type;
            while (specificType != null)
            {
                if (includeAbstractTypes || !specificType.Abstract)
                {
                    yield return specificType;
                }

                specificType = specificType.BaseType as EntityType;
                //The cast is guaranteed to work. See use of GetItems<T_EdmType> in GetTypesAndSubTypesOf()
            }
        }

        // <summary>
        // Builds an undirected graph (represented as a directional graph with reciprocal navigation edges) of the all the types in the workspace.
        // This is used to traverse inheritance hierarchy up and down.
        // O(n), where n=number of types
        // </summary>
        // <returns> A dictionary of type t -> set of types {s}, such that there is an edge between t and elem(s) iff t and s are related DIRECTLY via inheritance (child or parent type) </returns>
        internal static Dictionary<EntityType, Set<EntityType>> BuildUndirectedGraphOfTypes(EdmItemCollection edmItemCollection)
        {
            var graph = new Dictionary<EntityType, Set<EntityType>>();

            IEnumerable<EntityType> typesInWorkSpace = edmItemCollection.GetItems<EntityType>();
            foreach (var childType in typesInWorkSpace)
            {
                if (childType.BaseType == null) //root type
                {
                    continue;
                }

                var parentType = childType.BaseType as EntityType;
                Debug.Assert(parentType != null, "Parent type not Entity Type ??");

                AddDirectedEdgeBetweenEntityTypes(graph, childType, parentType);
                AddDirectedEdgeBetweenEntityTypes(graph, parentType, childType);
            }

            return graph;
        }

        // <summary>
        // is A parent of b?
        // </summary>
        internal static bool IsParentOf(EntityType a, EntityType b)
        {
            var parent = b.BaseType as EntityType;

            while (parent != null)
            {
                if (parent.EdmEquals(a))
                {
                    return true;
                }
                else
                {
                    parent = parent.BaseType as EntityType;
                }
            }
            return false;
        }

        // <summary>
        // Add an edge a --> b
        // Assumes edge does not exist
        // O(1)
        // </summary>
        private static void AddDirectedEdgeBetweenEntityTypes(Dictionary<EntityType, Set<EntityType>> graph, EntityType a, EntityType b)
        {
            Set<EntityType> references;
            if (graph.ContainsKey(a))
            {
                references = graph[a];
            }
            else
            {
                references = new Set<EntityType>();
                graph.Add(a, references);
            }

            Debug.Assert(!references.Contains(b), "Dictionary already has a --> b reference");
            references.Add(b);
        }

        // <summary>
        // Checks wither the given AssociationEnd's keys are sufficient for identifying a unique tuple in the AssociationSet.
        // This is possible because refconstraints make certain Keys redundant. We subtract such redundant key sof "other" ends
        // and see if what is left is contributed only from the given end's keys.
        // </summary>
        [SuppressMessage("Microsoft.Security", "CA2140:TransparentMethodsMustNotReferenceCriticalCode",
            Justification = "Based on Bug VSTS Pioneer #433188: IsVisibleOutsideAssembly is wrong on generic instantiations.")]
        internal static bool DoesEndKeySubsumeAssociationSetKey(
            AssociationSet assocSet, AssociationEndMember thisEnd, HashSet<Pair<EdmMember, EntityType>> associationkeys)
        {
            var assocType = assocSet.ElementType;
            var thisEndsEntityType = (EntityType)((RefType)thisEnd.TypeUsage.EdmType).ElementType;

            var thisEndKeys = new HashSet<Pair<EdmMember, EntityType>>(
                thisEndsEntityType.KeyMembers.Select(edmMember => new Pair<EdmMember, EntityType>(edmMember, thisEndsEntityType)));

            foreach (var constraint in assocType.ReferentialConstraints)
            {
                IEnumerable<EdmMember> otherEndProperties;
                EntityType otherEndType;

                if (thisEnd.Equals(constraint.ToRole))
                {
                    otherEndProperties = Helpers.AsSuperTypeList<EdmProperty, EdmMember>(constraint.FromProperties);
                    otherEndType = (EntityType)((RefType)(constraint.FromRole).TypeUsage.EdmType).ElementType;
                }
                else if (thisEnd.Equals(constraint.FromRole))
                {
                    otherEndProperties = Helpers.AsSuperTypeList<EdmProperty, EdmMember>(constraint.ToProperties);
                    otherEndType = (EntityType)((RefType)(constraint.ToRole).TypeUsage.EdmType).ElementType;
                }
                else
                {
                    //this end not part of the referential constraint
                    continue;
                }

                //Essentially ref constraints is an equality condition, so remove redundant members from entity set key
                foreach (var member in otherEndProperties)
                {
                    associationkeys.Remove(new Pair<EdmMember, EntityType>(member, otherEndType));
                }
            }

            //Now that all redundant members have been removed, is thisEnd the key of the entity set?
            return associationkeys.IsSubsetOf(thisEndKeys);
        }

        // effects: Returns true if end forms a key in relationshipSet
        internal static bool DoesEndFormKey(AssociationSet associationSet, AssociationEndMember end)
        {
            // Look at all other ends. if their multiplicities are at most 1, return true
            foreach (AssociationEndMember endMember in associationSet.ElementType.Members)
            {
                if (endMember.Equals(end) == false
                    &&
                    endMember.RelationshipMultiplicity == RelationshipMultiplicity.Many) // some other end has multiplicity 0..*
                {
                    return false;
                }
            }
            return true;
        }

        // effects: Returns true if extent is at one of the ends of relationshipSet
        internal static bool IsExtentAtSomeRelationshipEnd(AssociationSet relationshipSet, EntitySetBase extent)
        {
            if (Helper.IsEntitySet(extent))
            {
                return GetSomeEndForEntitySet(relationshipSet, extent) != null;
            }
            return false;
        }

        // effects: Returns some end corresponding to entity set in
        // association set. If no such end exists, return null
        internal static AssociationEndMember GetSomeEndForEntitySet(AssociationSet associationSet, EntitySetBase entitySet)
        {
            foreach (var associationEnd in associationSet.AssociationSetEnds)
            {
                if (associationEnd.EntitySet.Equals(entitySet))
                {
                    return associationEnd.CorrespondingAssociationEndMember;
                }
            }
            return null;
        }

        // requires: entitySet1 and entitySet2 belong to the same container
        // effects: Returns the associations that occur between entitySet1
        // and entitySet2. If none is found, returns an empty set
        internal static List<AssociationSet> GetAssociationsForEntitySets(EntitySet entitySet1, EntitySet entitySet2)
        {
            DebugCheck.NotNull(entitySet1);
            DebugCheck.NotNull(entitySet2);
            Debug.Assert(
                entitySet1.EntityContainer == entitySet2.EntityContainer, "EntityContainer must be the same for both the entity sets");

            var result = new List<AssociationSet>();

            foreach (var extent in entitySet1.EntityContainer.BaseEntitySets)
            {
                if (Helper.IsRelationshipSet(extent))
                {
                    var assocSet = (AssociationSet)extent;
                    if (IsExtentAtSomeRelationshipEnd(assocSet, entitySet1)
                        &&
                        IsExtentAtSomeRelationshipEnd(assocSet, entitySet2))
                    {
                        result.Add(assocSet);
                    }
                }
            }
            return result;
        }

        // requires: entitySet
        // effects: Returns the associations that occur between entitySet
        // and other entitySets. If none is found, returns an empty set
        internal static List<AssociationSet> GetAssociationsForEntitySet(EntitySetBase entitySet)
        {
            DebugCheck.NotNull(entitySet);

            var result = new List<AssociationSet>();

            foreach (var extent in entitySet.EntityContainer.BaseEntitySets)
            {
                if (Helper.IsRelationshipSet(extent))
                {
                    var assocSet = (AssociationSet)extent;
                    if (IsExtentAtSomeRelationshipEnd(assocSet, entitySet))
                    {
                        result.Add(assocSet);
                    }
                }
            }
            return result;
        }

        // effects: Returns true iff superType is an ancestor of subType in
        // the type hierarchy or superType and subType are the same
        internal static bool IsSuperTypeOf(EdmType superType, EdmType subType)
        {
            var currentType = subType;
            while (currentType != null)
            {
                if (currentType.Equals(superType))
                {
                    return true;
                }
                currentType = currentType.BaseType;
            }
            return false;
        }

        // determines whether the given member is a key of an entity set
        internal static bool IsPartOfEntityTypeKey(EdmMember member)
        {
            if (Helper.IsEntityType(member.DeclaringType)
                &&
                Helper.IsEdmProperty(member))
            {
                return ((EntityType)member.DeclaringType).KeyMembers.Contains(member);
            }

            return false;
        }

        // Given a type usage, returns the element type (unwraps collections) 
        internal static TypeUsage GetElementType(TypeUsage typeUsage)
        {
            if (BuiltInTypeKind.CollectionType
                == typeUsage.EdmType.BuiltInTypeKind)
            {
                var elementType = ((CollectionType)typeUsage.EdmType).TypeUsage;
                // recursively unwrap
                return GetElementType(elementType);
            }
            return typeUsage;
        }

        internal static int GetLowerBoundOfMultiplicity(RelationshipMultiplicity multiplicity)
        {
            if (multiplicity == RelationshipMultiplicity.Many
                || multiplicity == RelationshipMultiplicity.ZeroOrOne)
            {
                return 0;
            }
            else
            {
                return 1;
            }
        }

        internal static int? GetUpperBoundOfMultiplicity(RelationshipMultiplicity multiplicity)
        {
            if (multiplicity == RelationshipMultiplicity.One
                || multiplicity == RelationshipMultiplicity.ZeroOrOne)
            {
                return 1;
            }
            else
            {
                return null;
            }
        }

        // effects: Returns all the concurrency token members in superType and its subtypes
        internal static Set<EdmMember> GetConcurrencyMembersForTypeHierarchy(EntityTypeBase superType, EdmItemCollection edmItemCollection)
        {
            var result = new Set<EdmMember>();
            foreach (StructuralType type in GetTypeAndSubtypesOf(superType, edmItemCollection, true /*includeAbstractTypes */))
            {
                // Go through all the members -- Can call Members instead of AllMembers since we are
                // running through the whole hierarchy
                foreach (var member in type.Members)
                {
                    // check for the concurrency facet
                    var concurrencyMode = GetConcurrencyMode(member);
                    if (concurrencyMode == ConcurrencyMode.Fixed)
                    {
                        result.Add(member);
                    }
                }
            }
            return result;
        }

        // Determines whether the given member is declared as a concurrency property
        internal static ConcurrencyMode GetConcurrencyMode(EdmMember member)
        {
            return GetConcurrencyMode(member.TypeUsage);
        }

        // Determines whether the given member is declared as a concurrency property
        internal static ConcurrencyMode GetConcurrencyMode(TypeUsage typeUsage)
        {
            Facet concurrencyFacet;
            if (typeUsage.Facets.TryGetValue(EdmProviderManifest.ConcurrencyModeFacetName, false, out concurrencyFacet)
                && concurrencyFacet.Value != null)
            {
                var concurrencyMode = (ConcurrencyMode)concurrencyFacet.Value;
                return concurrencyMode;
            }
            return ConcurrencyMode.None;
        }

        // Determines the store generated pattern for this member
        internal static StoreGeneratedPattern GetStoreGeneratedPattern(EdmMember member)
        {
            Facet storeGeneratedFacet;
            if (member.TypeUsage.Facets.TryGetValue(EdmProviderManifest.StoreGeneratedPatternFacetName, false, out storeGeneratedFacet)
                && storeGeneratedFacet.Value != null)
            {
                var pattern = (StoreGeneratedPattern)storeGeneratedFacet.Value;

                return pattern;
            }

            return StoreGeneratedPattern.None;
        }

        // <summary>
        // Check if all the SchemaErrors have the serverity of SchemaErrorSeverity.Warning
        // </summary>
        internal static bool CheckIfAllErrorsAreWarnings(IList<EdmSchemaError> schemaErrors)
        {
            var length = schemaErrors.Count;
            for (var i = 0; i < length; ++i)
            {
                var error = schemaErrors[i];
                if (error.Severity
                    != EdmSchemaErrorSeverity.Warning)
                {
                    return false;
                }
            }
            return true;
        }

        [SuppressMessage("Microsoft.Cryptographic.Standard", "CA5350:Microsoft.Cryptographic.Standard",
            Justification =
                "MD5CryptoServiceProvider is not used for cryptography/security purposes and we do it only for v1 and v1.1 for compatibility reasons."
            )]
        internal static HashAlgorithm CreateMetadataHashAlgorithm(double schemaVersion)
        {
            HashAlgorithm hashAlgorithm;
            if (schemaVersion < XmlConstants.EdmVersionForV2)
            {
                // v1 and v1.1 use old hash to remain compatible
                hashAlgorithm = new MD5CryptoServiceProvider();
            }
            else
            {
                // v2 and above use a FIPS approved provider 
                // so that when FIPS only is enforced by the OS 
                // we still work
                hashAlgorithm = CreateSHA256HashAlgorithm();
            }
            return hashAlgorithm;
        }

        internal static SHA256 CreateSHA256HashAlgorithm()
        {
            SHA256 sha256HashAlgorith;
            try
            {
                // use the FIPS compliant SHA256 implementation
                sha256HashAlgorith = new SHA256CryptoServiceProvider();
            }
            catch (PlatformNotSupportedException)
            {
                // the FIPS compliant (and faster) algorith was not available, create the managed version
                // this will throw if FIPS only is enforced
                sha256HashAlgorith = new SHA256Managed();
            }

            return sha256HashAlgorith;
        }

        internal static TypeUsage ConvertStoreTypeUsageToEdmTypeUsage(TypeUsage storeTypeUsage)
        {
            var edmTypeUsage = storeTypeUsage.ModelTypeUsage.ShallowCopy(FacetValues.NullFacetValues);

            // we don't reason the facets during the function resolution any more

            return edmTypeUsage;
        }

        internal static byte GetPrecision(this TypeUsage type)
        {
            return type.GetFacetValue<byte>(DbProviderManifest.PrecisionFacetName);
        }

        internal static byte GetScale(this TypeUsage type)
        {
            return type.GetFacetValue<byte>(DbProviderManifest.ScaleFacetName);
        }

        internal static int GetMaxLength(this TypeUsage type)
        {
            return type.GetFacetValue<int>(DbProviderManifest.MaxLengthFacetName);
        }

        internal static T GetFacetValue<T>(this TypeUsage type, string facetName)
        {
            return (T)type.Facets[facetName].Value;
        }

        #region NavigationPropertyAccessor Helpers

        internal static NavigationPropertyAccessor GetNavigationPropertyAccessor(
            EntityType sourceEntityType, AssociationEndMember sourceMember, AssociationEndMember targetMember)
        {
            Debug.Assert(
                sourceEntityType.DataSpace == DataSpace.OSpace && sourceEntityType.ClrType != null,
                "sourceEntityType must contain an ospace type");
            return GetNavigationPropertyAccessor(
                sourceEntityType, sourceMember.DeclaringType.FullName, sourceMember.Name, targetMember.Name);
        }

        internal static NavigationPropertyAccessor GetNavigationPropertyAccessor(
            EntityType entityType, string relationshipType, string fromName, string toName)
        {
            NavigationProperty navigationProperty;
            if (entityType.TryGetNavigationProperty(relationshipType, fromName, toName, out navigationProperty))
            {
                return navigationProperty.Accessor;
            }
            else
            {
                return NavigationPropertyAccessor.NoNavigationProperty;
            }
        }

        #endregion
    }
}
