namespace GroupQuota
{
    using Azure;
    using Azure.Core;
    using Azure.Identity;
    using Azure.ResourceManager;
    using Azure.ResourceManager.ManagementGroups;
    using Azure.ResourceManager.Quota;
    using Azure.ResourceManager.Quota.Models;
    using System.ClientModel.Primitives;

    internal class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                Console.WriteLine("Starting Group Quota Enforcement");
                await RunEnableGroupQuotaEnforcement();
                Console.WriteLine("Group Quota Enforcement completed successfully");
            }
            catch (RequestFailedException ex)
            {
                Console.WriteLine($"Operation failed - Status: {ex.Status}, ErrorCode: {ex.ErrorCode}, Message: {ex.Message}");
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An unhandled error occurred during execution: {ex.Message}");
                Environment.Exit(1);
            }
        }

        /// <summary>
        /// This method demonstrates how to enable group quota enforcement for quota group/allocation group in a management group
        /// <para>The steps include:</para>
        /// <para>1. Create a group quota entity</para>
        /// <para>2. Add the subscription to the allocation group</para>
        /// <para>3. Enable enforcement on the allocation group</para>
        /// <para>4. Clean up resources (for demonstration purposes)</para>
        /// </summary>
        private static async Task RunEnableGroupQuotaEnforcement()
        {
            // Replace with your actual Azure subscription ID
            // Go to aka.ms/sharedlimit to onboard your subscription if not already done
            string defaultSubscriptionId = "f9f44809-a71d-4ea0-9635-77ac7bbfd319";
            
            // Replace with your management group ID
            string managementGroupId = "testmg";
            
            string groupQuotaName = "sdk-enforcement-test-group";
            string resourceProviderName = "Microsoft.Compute";

            // Replace with your target Azure location where you would like to enable enforcement
            AzureLocation location = new AzureLocation("westus");

            Console.WriteLine($"Configuration - Subscription: {defaultSubscriptionId}, Management Group: {managementGroupId}, Group Name: {groupQuotaName}");

            Console.WriteLine("Initializing ARM client with DefaultAzureCredential");
            
            var client = new ArmClient(
                credential: new DefaultAzureCredential(),
                defaultSubscriptionId: defaultSubscriptionId);

            Console.WriteLine("ARM client initialized successfully");

            var groupQuotaEntity = await CreateGroupQuotaAsync(client, managementGroupId, groupQuotaName);

            // Add subscription to allocation group. This is required before enabling enforcement
            await AddSubscriptionToGroupAsync(groupQuotaEntity, defaultSubscriptionId);

            var enforcedGroupName = await EnableEnforcementAsync(groupQuotaEntity, resourceProviderName, location, groupQuotaName);

            await CleanupResourcesAsync(client, managementGroupId, defaultSubscriptionId, groupQuotaName, enforcedGroupName, groupQuotaEntity);
        }

        private static async Task<GroupQuotaEntityResource> CreateGroupQuotaAsync(ArmClient client, string managementGroupId, string groupQuotaName)
        {
            try
            {
                Console.WriteLine($"Creating group quota entity '{groupQuotaName}' in management group '{managementGroupId}'");

                ResourceIdentifier managementGroupResourceId = ManagementGroupResource.CreateResourceIdentifier(managementGroupId);
                ManagementGroupResource managementGroupResource = client.GetManagementGroupResource(managementGroupResourceId);
                GroupQuotaEntityCollection groupQuotaEntityCollection = managementGroupResource.GetGroupQuotaEntities();

                GroupQuotaEntityData createGroupQuotaRequest = new GroupQuotaEntityData()
                {
                    Properties = new GroupQuotasEntityProperties()
                    {
                        DisplayName = groupQuotaName
                    }
                };

                Console.WriteLine("Submitting create or update request for group quota entity");
                var operation = await groupQuotaEntityCollection.CreateOrUpdateAsync(WaitUntil.Completed, groupQuotaName, createGroupQuotaRequest);

                ResourceIdentifier groupQuotaEntityResourceId = GroupQuotaEntityResource.CreateResourceIdentifier(managementGroupId, groupQuotaName);
                GroupQuotaEntityResource groupQuotaEntity = client.GetGroupQuotaEntityResource(groupQuotaEntityResourceId);

                var group = await groupQuotaEntity.GetAsync();
                Console.WriteLine($"Successfully created group quota entity with display name: '{group.Value.Data.Properties.DisplayName}'");

                return groupQuotaEntity;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to create group quota entity '{groupQuotaName}' in management group '{managementGroupId}' - {ex.Message}");
                throw;
            }
        }

        private static async Task AddSubscriptionToGroupAsync(GroupQuotaEntityResource groupQuotaEntity, string subscriptionId)
        {
            try
            {
                Console.WriteLine($"Adding subscription '{subscriptionId}' to allocation group");

                GroupQuotaSubscriptionCollection collection = groupQuotaEntity.GetGroupQuotaSubscriptions();
                await collection.CreateOrUpdateAsync(WaitUntil.Completed, subscriptionId);

                Console.WriteLine($"Successfully added subscription '{subscriptionId}' to allocation group");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to add subscription '{subscriptionId}' to allocation group - {ex.Message}");
                throw;
            }
        }

        private static async Task<string> EnableEnforcementAsync(GroupQuotaEntityResource groupQuotaEntity, string resourceProviderName, AzureLocation location, string groupQuotaName)
        {
            try
            {
                Console.WriteLine($"Enabling enforcement for group quota '{groupQuotaName}' with resource provider '{resourceProviderName}' in location '{location}'");

                GroupQuotasEnforcementStatusCollection groupQuotaEnforcementStatusCollection = groupQuotaEntity.GetGroupQuotasEnforcementStatuses();

                GroupQuotasEnforcementStatusData data = new GroupQuotasEnforcementStatusData
                {
                    Properties = new GroupQuotasEnforcementStatusProperties
                    {
                        EnforcementEnabled = EnforcementState.Enabled,
                    },
                };

                Console.WriteLine("Submitting enforcement enable request");
                ArmOperation<GroupQuotasEnforcementStatusResource> lro = await groupQuotaEnforcementStatusCollection.CreateOrUpdateAsync(WaitUntil.Started, resourceProviderName, location, data);
                
                string? enforcedGroupName = null;
                Response rawResponse = lro.GetRawResponse();

                Console.WriteLine($"Initial enforcement operation response status: {rawResponse.Status}, response content: {rawResponse.Content}");

                // Use ModelReaderWriter to deserialize the data portion of the response
                var enforcementStatusData = ModelReaderWriter.Read<GroupQuotasEnforcementStatusData>(
                    rawResponse.Content,
                    ModelReaderWriterOptions.Json);

                enforcedGroupName = enforcementStatusData!.Properties.EnforcedGroupName;
                Console.WriteLine($"Enforced group name: {enforcedGroupName}");
                Console.WriteLine($"Provisioning State: {enforcementStatusData.Properties.ProvisioningState}");

                do
                {
                    Console.WriteLine("Enforcement operation in progress... polling for status");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    await lro.UpdateStatusAsync();
                }
                while (!lro.HasCompleted);

                var finalResult = lro.Value.Data;
                Console.WriteLine(
                    $"Enforcement operation completed - Provisioning State: {finalResult.Properties?.ProvisioningState}, " +
                    $"Enforcement Enabled: {finalResult.Properties?.EnforcementEnabled}");

                // The enforced group name is a combination of the allocation group name and the location name in the format: {groupQuotaName}-{location}
                // For example, if the allocation group name is "sdk-enforcement-test-group" and the location is "westus", the enforced group name will be "sdk-enforcement-test-group-westus"
                return enforcedGroupName ?? $"{groupQuotaName}-{location}";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to enable enforcement for group quota '{groupQuotaName}' with provider '{resourceProviderName}' in location '{location}' - {ex.Message}");
                throw;
            }
        }

        private static async Task CleanupResourcesAsync(ArmClient client, string managementGroupId, string subscriptionId, string groupQuotaName, string enforcedGroupName, GroupQuotaEntityResource groupQuotaEntity)
        {
            Console.WriteLine("Starting cleanup operations");

            // Delete subscription from enforced group
            // This must be done before deleting the enforced group
            await DeleteSubscriptionFromEnforcedGroupAsync(client, managementGroupId, enforcedGroupName, subscriptionId);

            // Delete subscription from allocation group
            await DeleteSubscriptionFromAllocationGroupAsync(client, managementGroupId, groupQuotaName, subscriptionId);

            // Delete enforced group
            await DeleteEnforcedGroupAsync(client, managementGroupId, enforcedGroupName);

            // Delete allocation group
            await DeleteAllocationGroupAsync(groupQuotaEntity, groupQuotaName);

            Console.WriteLine("Cleanup operations completed successfully");
        }

        private static async Task DeleteSubscriptionFromEnforcedGroupAsync(ArmClient client, string managementGroupId, string enforcedGroupName, string subscriptionId)
        {
            try
            {
                Console.WriteLine($"Deleting subscription '{subscriptionId}' from enforced group '{enforcedGroupName}'");

                ResourceIdentifier enforcedGroupQuotaSubscriptionResourceId = GroupQuotaSubscriptionResource.CreateResourceIdentifier(managementGroupId, enforcedGroupName, subscriptionId);
                GroupQuotaSubscriptionResource enforceGroupQuotaSubscriptionResource = client.GetGroupQuotaSubscriptionResource(enforcedGroupQuotaSubscriptionResourceId);
                await enforceGroupQuotaSubscriptionResource.DeleteAsync(WaitUntil.Completed);

                Console.WriteLine($"Successfully deleted subscription '{subscriptionId}' from enforced group '{enforcedGroupName}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to remove subscription '{subscriptionId}' from enforced group '{enforcedGroupName}' in management group '{managementGroupId}' - {ex.Message}");
                throw;
            }
        }

        private static async Task DeleteSubscriptionFromAllocationGroupAsync(ArmClient client, string managementGroupId, string groupQuotaName, string subscriptionId)
        {
            try
            {
                Console.WriteLine($"Deleting subscription '{subscriptionId}' from allocation group '{groupQuotaName}'");

                ResourceIdentifier allocationGroupQuotaSubscriptionResourceId = GroupQuotaSubscriptionResource.CreateResourceIdentifier(managementGroupId, groupQuotaName, subscriptionId);
                GroupQuotaSubscriptionResource allocationGroupQuotaSubscriptionResource = client.GetGroupQuotaSubscriptionResource(allocationGroupQuotaSubscriptionResourceId);
                await allocationGroupQuotaSubscriptionResource.DeleteAsync(WaitUntil.Completed);

                Console.WriteLine($"Successfully deleted subscription '{subscriptionId}' from allocation group '{groupQuotaName}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to remove subscription '{subscriptionId}' from allocation group '{groupQuotaName}' in management group '{managementGroupId}' - {ex.Message}");
                throw;
            }
        }

        private static async Task DeleteEnforcedGroupAsync(ArmClient client, string managementGroupId, string enforcedGroupName)
        {
            try
            {
                Console.WriteLine($"Deleting enforced group '{enforcedGroupName}'");

                ResourceIdentifier enforcedGroupQuotaEntityResourceId = GroupQuotaEntityResource.CreateResourceIdentifier(managementGroupId, enforcedGroupName);
                GroupQuotaEntityResource enforcedGroupQuotaEntity = client.GetGroupQuotaEntityResource(enforcedGroupQuotaEntityResourceId);
                await enforcedGroupQuotaEntity.DeleteAsync(WaitUntil.Completed);

                Console.WriteLine($"Successfully deleted enforced group '{enforcedGroupName}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to delete enforced group '{enforcedGroupName}' from management group '{managementGroupId}' - {ex.Message}");
                throw;
            }
        }

        private static async Task DeleteAllocationGroupAsync(GroupQuotaEntityResource groupQuotaEntity, string groupQuotaName)
        {
            try
            {
                Console.WriteLine($"Deleting allocation group '{groupQuotaName}'");

                await groupQuotaEntity.DeleteAsync(WaitUntil.Started);

                Console.WriteLine($"Successfully deleted allocation group '{groupQuotaName}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to delete allocation group '{groupQuotaName}' - {ex.Message}");
                throw;
            }
        }
    }
}
