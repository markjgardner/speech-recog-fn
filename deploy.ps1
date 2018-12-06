param(
  [Parameter(Mandatory=$true)]
  $resourceGroupName,
  
  [Parameter(Mandatory=$true)]
  $functionName,

  [Parameter(Mandatory=$true)]
  $subscriptionId 
)

write-output("Connecting to the target azure subscription...")
Connect-AzureRmAccount
Select-AzureRmSubscription -Subscription $subscriptionId

$params = @{
    appName = "$functionName"
}

$rg = Get-AzureRmResourceGroup -Name $resourceGroupName

write-output "Deploying resources..."
New-AzureRmResourceGroupDeployment -Name "Speech-recog-function-deploy" -ResourceGroupName $resourceGroupName -TemplateParameterObject $params -TemplateFile "armTemplate.json" -Mode Incremental
write-output "completed"

$key = Get-AzureRmCognitiveServicesAccountKey -ResourceGroupName $resourceGroupName -Name 'speechApi'
Write-Output "Please register the following subscription key at https://$($rg.Location).cris.ai/subscriptions/create"
Write-Output "-------------------------------------------"
Write-Output $key.Key1
Write-Output "-------------------------------------------"
write-output "Press any key to continue."
[void][System.Console]::ReadKey($true)

$start = Get-Date
$end = (Get-Date).AddYears(1)
$policyName = "getBlob"
$storage = Get-AzureRmStorageAccount -ResourceGroupName $resourceGroupName -Name "$($functionName.ToLower())sa"
$policy = New-AzureStorageContainerStoredAccessPolicy -Context $storage.Context -Container "recordings" -Policy $policyName -Permission "rl" -StartTime $start -ExpiryTime $end 

$app = Get-AzureRmWebApp -ResourceGroupName $resourceGroupName -Name $functionName
$appsettings = @{}
foreach ($kvp in $app.SiteConfig.AppSettings) {
  $appsettings.Add($kvp.name, $kvp.value)
}
if (-not $appsettings["SpeechApiToken"]) { $appsettings.Add("SpeechApiToken", $key.Key1); }
if (-not $appsettings["BlobContainerPolicyName"]) { $appsettings.Add("BlobContainerPolicyName", $policyName); }

write-output "Updating app settings..."
$updated = Set-AzureRmWebApp -ResourceGroupName $resourceGroupName -Name $functionName -AppSettings $appsettings
write-output "completed"
